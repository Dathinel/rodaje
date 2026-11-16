using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;

namespace Rodaje.VulkanVideo;

public sealed unsafe class DecoderRunner
{
    private readonly VulkanContext _vk;
    private readonly bool _forceUpload;
    private readonly string _camino;

    public int Nv12Frames { get; private set; }
    public int P010Frames { get; private set; }
    public int TotalFrames => Nv12Frames + P010Frames;
    public double Fps { get; private set; }
    public double P50Ms { get; private set; }
    public double P95Ms { get; private set; }
    public int NormalCpuTransfers { get; private set; } = 0;
    public double StartMemoryMb { get; private set; }
    public double MidMemoryMb { get; private set; }
    public double EndMemoryMb { get; private set; }
    public int VerifiedFramesCount { get; private set; }
    public double AvgMae255 { get; private set; }
    public double AvgPsnrDb { get; private set; }
    public bool ThresholdPassed { get; private set; } = true;
    public const double MaeThreshold = 2.0 / 255.0; // Fixed threshold: MAE <= 2/255

    public FormatoExactoInfo Nv12ExactStats { get; } = new();
    public FormatoExactoInfo P010ExactStats { get; } = new();
    public bool ExactComparisonPassed =>
        (Nv12ExactStats.CuadrosComparados == 0 || Nv12ExactStats.CuadrosIdenticos == Nv12ExactStats.CuadrosComparados) &&
        (P010ExactStats.CuadrosComparados == 0 || P010ExactStats.CuadrosIdenticos == P010ExactStats.CuadrosComparados) &&
        (Nv12ExactStats.CuadrosComparados > 0 || P010ExactStats.CuadrosComparados > 0);

    private static readonly AVCodecContext_get_format s_getFormatDelegate = GetFormatVulkan;

    public DecoderRunner(VulkanContext vk, bool forceUpload)
    {
        _vk = vk;
        _forceUpload = forceUpload;
        _camino = (vk.SupportsVulkanVideo && !forceUpload) ? "vulkan-video" : "subida";
    }

    public void Run(string h264Path, string hevcPath, int targetSeconds)
    {
        StartMemoryMb = GetProcessMemoryMb();

        Dictionary<long, ReferenceFrame>? h264Refs = null;
        Dictionary<long, ReferenceFrame>? hevcRefs = null;
        if (_camino == "vulkan-video")
        {
            Console.WriteLine("Extrayendo cuadros de referencia por software para verificación de calidad...");
            h264Refs = ReferenceExtractor.ExtractReferences(h264Path, isHevc: false, interval: 30);
            hevcRefs = ReferenceExtractor.ExtractReferences(hevcPath, isHevc: true, interval: 30);
            Console.WriteLine($"Referencias generadas: H.264 = {h264Refs.Count}, HEVC = {hevcRefs.Count}");
        }

        var frameDurationsMs = new List<double>();
        var samplePaths = new[] { (h264Path, false, h264Refs), (hevcPath, true, hevcRefs) };

        var overallSw = Stopwatch.StartNew();
        double targetSecondsDbl = targetSeconds;
        bool recordedMidMemory = false;

        double sumMae = 0.0;
        double sumPsnr = 0.0;

        byte[] gpuRgba = new byte[VulkanContext.TargetBufferSize];
        byte[] swRgba = new byte[VulkanContext.TargetBufferSize];

        int loopCount = 0;
        while (overallSw.Elapsed.TotalSeconds < targetSecondsDbl)
        {
            loopCount++;
            foreach (var (path, isHevc, refs) in samplePaths)
            {
                if (overallSw.Elapsed.TotalSeconds >= targetSecondsDbl) break;

                RunSample(path, isHevc, refs, overallSw, targetSecondsDbl, ref recordedMidMemory,
                          frameDurationsMs, gpuRgba, swRgba, ref sumMae, ref sumPsnr);
            }
        }

        overallSw.Stop();
        EndMemoryMb = GetProcessMemoryMb();
        if (!recordedMidMemory) MidMemoryMb = EndMemoryMb;

        if (TotalFrames > 0 && overallSw.Elapsed.TotalSeconds > 0)
        {
            Fps = TotalFrames / overallSw.Elapsed.TotalSeconds;
        }

        if (frameDurationsMs.Count > 0)
        {
            frameDurationsMs.Sort();
            int idx50 = (int)(frameDurationsMs.Count * 0.50);
            int idx95 = (int)(frameDurationsMs.Count * 0.95);
            P50Ms = frameDurationsMs[Math.Clamp(idx50, 0, frameDurationsMs.Count - 1)];
            P95Ms = frameDurationsMs[Math.Clamp(idx95, 0, frameDurationsMs.Count - 1)];
        }

        if (VerifiedFramesCount > 0)
        {
            AvgMae255 = sumMae / VerifiedFramesCount;
            AvgPsnrDb = sumPsnr / VerifiedFramesCount;
            if (AvgMae255 > MaeThreshold)
            {
                ThresholdPassed = false;
            }
        }
    }

    private void RunSample(
        string path,
        bool isHevc,
        Dictionary<long, ReferenceFrame>? refs,
        Stopwatch overallSw,
        double targetSecondsDbl,
        ref bool recordedMidMemory,
        List<double> frameDurationsMs,
        byte[] gpuRgba,
        byte[] swRgba,
        ref double sumMae,
        ref double sumPsnr)
    {
        AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
        if (ffmpeg.avformat_open_input(&fmtCtx, path, null, null) < 0) return;
        if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            return;
        }

        int videoStream = -1;
        for (int i = 0; i < fmtCtx->nb_streams; i++)
        {
            if (fmtCtx->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
            {
                videoStream = i;
                break;
            }
        }

        if (videoStream < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            return;
        }

        AVCodecParameters* codecpar = fmtCtx->streams[videoStream]->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);

        AVCodecContext* cctx = null;
        AVBufferRef* uploadFramesRef = null;

        if (_camino == "vulkan-video")
        {
            cctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(cctx, codecpar);
            cctx->hw_device_ctx = ffmpeg.av_buffer_ref(_vk.HwDeviceRef);
            cctx->get_format = s_getFormatDelegate;
            int ret = ffmpeg.avcodec_open2(cctx, codec, null);
            if (ret < 0)
            {
                ffmpeg.avformat_close_input(&fmtCtx);
                return;
            }
        }
        else
        {
            // Fallback upload mode
            cctx = ffmpeg.avcodec_alloc_context3(codec);
            ffmpeg.avcodec_parameters_to_context(cctx, codecpar);
            int ret = ffmpeg.avcodec_open2(cctx, codec, null);
            if (ret < 0)
            {
                ffmpeg.avformat_close_input(&fmtCtx);
                return;
            }

            uploadFramesRef = ffmpeg.av_hwframe_ctx_alloc(_vk.HwDeviceRef);
            var fc = (AVHWFramesContext*)uploadFramesRef->data;
            fc->format = AVPixelFormat.AV_PIX_FMT_VULKAN;
            fc->sw_format = isHevc ? AVPixelFormat.AV_PIX_FMT_P010LE : AVPixelFormat.AV_PIX_FMT_NV12;
            fc->width = codecpar->width;
            fc->height = codecpar->height;

            var vkfc = (FfmpegInterop.AVVulkanFramesContext*)fc->hwctx;
            vkfc->usage = (uint)(TerraFX.Interop.Vulkan.VkImageUsageFlags.VK_IMAGE_USAGE_SAMPLED_BIT |
                                 TerraFX.Interop.Vulkan.VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_DST_BIT |
                                 TerraFX.Interop.Vulkan.VkImageUsageFlags.VK_IMAGE_USAGE_TRANSFER_SRC_BIT);

            int fcRet = ffmpeg.av_hwframe_ctx_init(uploadFramesRef);
            if (fcRet < 0)
            {
                ffmpeg.avcodec_free_context(&cctx);
                ffmpeg.avformat_close_input(&fmtCtx);
                return;
            }
        }

        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* decFrame = ffmpeg.av_frame_alloc();
        AVFrame* uploadFrame = ffmpeg.av_frame_alloc();
        AVFrame* swConvertedFrame = ffmpeg.av_frame_alloc();
        SwsContext* uploadSws = null;

        SwsContext* sws = null;
        byte*[] dstData = new byte*[4];
        int[] dstLinesize = new int[] { VulkanContext.TargetWidth * 4, 0, 0, 0 };

        int frameIdx = 0;

        while (ffmpeg.av_read_frame(fmtCtx, pkt) >= 0)
        {
            if (overallSw.Elapsed.TotalSeconds >= targetSecondsDbl) break;

            if (!recordedMidMemory && overallSw.Elapsed.TotalSeconds >= (targetSecondsDbl / 2.0))
            {
                MidMemoryMb = GetProcessMemoryMb();
                recordedMidMemory = true;
            }

            if (pkt->stream_index == videoStream)
            {
                ffmpeg.avcodec_send_packet(cctx, pkt);

                if (_camino == "vulkan-video")
                {
                    while (ffmpeg.avcodec_receive_frame(cctx, decFrame) == 0)
                    {
                        long frameStartTicks = Stopwatch.GetTimestamp();

                        ReferenceFrame? refFrame = null;
                        bool doVerify = (refs != null && refs.TryGetValue(decFrame->pts, out refFrame));

                        _vk.ProcessFrame(decFrame, doVerify, doVerify ? gpuRgba : null);

                        if (doVerify && refFrame != null)
                        {
                            // 1. RGBA quality comparison (MAE / PSNR)
                            CompareRgba(gpuRgba, refFrame.Rgba, ref sumMae, ref sumPsnr, isHevc, decFrame->pts);

                            // 2. Exact bit-for-bit plane comparison (NV12 / P010)
                            AVFrame* swDownloaded = ffmpeg.av_frame_alloc();
                            int tRet = ffmpeg.av_hwframe_transfer_data(swDownloaded, decFrame, 0);
                            if (tRet >= 0)
                            {
                                CompareExactPlanes(swDownloaded, refFrame, isHevc ? P010ExactStats : Nv12ExactStats, isHevc);
                            }
                            ffmpeg.av_frame_free(&swDownloaded);
                        }

                        ffmpeg.av_frame_unref(decFrame);

                        double frameMs = Stopwatch.GetElapsedTime(frameStartTicks).TotalMilliseconds;
                        frameDurationsMs.Add(frameMs);

                        if (isHevc) P010Frames++;
                        else Nv12Frames++;
                        frameIdx++;

                        if (overallSw.Elapsed.TotalSeconds >= targetSecondsDbl) break;
                    }
                }
                else
                {
                    // Fallback upload mode
                    while (ffmpeg.avcodec_receive_frame(cctx, decFrame) == 0)
                    {
                        long frameStartTicks = Stopwatch.GetTimestamp();

                        AVPixelFormat targetSwFmt = ((AVHWFramesContext*)uploadFramesRef->data)->sw_format;
                        AVFrame* frameToUpload = decFrame;
                        if ((AVPixelFormat)decFrame->format != targetSwFmt)
                        {
                            if (uploadSws == null)
                            {
                                uploadSws = ffmpeg.sws_getContext(
                                    decFrame->width, decFrame->height, (AVPixelFormat)decFrame->format,
                                    decFrame->width, decFrame->height, targetSwFmt,
                                    1 /* SWS_FAST_BILINEAR */, null, null, null);
                            }

                            swConvertedFrame->width = decFrame->width;
                            swConvertedFrame->height = decFrame->height;
                            swConvertedFrame->format = (int)targetSwFmt;
                            ffmpeg.av_frame_get_buffer(swConvertedFrame, 0);
                            ffmpeg.sws_scale(uploadSws, decFrame->data, decFrame->linesize, 0, decFrame->height,
                                             swConvertedFrame->data, swConvertedFrame->linesize);
                            frameToUpload = swConvertedFrame;
                        }

                        ffmpeg.av_hwframe_get_buffer(uploadFramesRef, uploadFrame, 0);
                        uploadFrame->width = frameToUpload->width;
                        uploadFrame->height = frameToUpload->height;
                        ffmpeg.av_hwframe_transfer_data(uploadFrame, frameToUpload, 0);

                        bool doVerify = (frameIdx % 30 == 0);
                        _vk.ProcessFrame(uploadFrame, doVerify, doVerify ? gpuRgba : null);

                        if (doVerify)
                        {
                            // Download back uploaded frame and compare with software frame
                            AVFrame* dlFrame = ffmpeg.av_frame_alloc();
                            int tRet = ffmpeg.av_hwframe_transfer_data(dlFrame, uploadFrame, 0);
                            if (tRet >= 0)
                            {
                                CompareExactPlanesRaw(dlFrame, frameToUpload, isHevc ? P010ExactStats : Nv12ExactStats, isHevc);
                            }
                            ffmpeg.av_frame_free(&dlFrame);

                            ScaleSoftwareFrame(decFrame, ref sws, dstData, dstLinesize, swRgba);
                            CompareRgba(gpuRgba, swRgba, ref sumMae, ref sumPsnr);
                        }

                        if (frameToUpload == swConvertedFrame)
                        {
                            ffmpeg.av_frame_unref(swConvertedFrame);
                        }

                        ffmpeg.av_frame_unref(uploadFrame);
                        ffmpeg.av_frame_unref(decFrame);

                        double frameMs = Stopwatch.GetElapsedTime(frameStartTicks).TotalMilliseconds;
                        frameDurationsMs.Add(frameMs);

                        if (isHevc) P010Frames++;
                        else Nv12Frames++;
                        frameIdx++;

                        if (overallSw.Elapsed.TotalSeconds >= targetSecondsDbl) break;
                    }
                }
            }
            ffmpeg.av_packet_unref(pkt);
        }

        if (sws != null) ffmpeg.sws_freeContext(sws);
        if (uploadSws != null) ffmpeg.sws_freeContext(uploadSws);

        ffmpeg.av_frame_free(&uploadFrame);
        ffmpeg.av_frame_free(&swConvertedFrame);
        ffmpeg.av_frame_free(&decFrame);
        ffmpeg.av_packet_free(&pkt);

        if (uploadFramesRef != null) ffmpeg.av_buffer_unref(&uploadFramesRef);
        if (cctx != null) ffmpeg.avcodec_free_context(&cctx);

        ffmpeg.avformat_close_input(&fmtCtx);
    }

    private static void CompareExactPlanes(
        AVFrame* hwDownloaded,
        ReferenceFrame refFrame,
        FormatoExactoInfo stats,
        bool isHevc)
    {
        int width = hwDownloaded->width;
        int height = hwDownloaded->height;
        int bytesPerSample = isHevc ? 2 : 1;
        int rowBytesY = width * bytesPerSample;
        int rowBytesUV = width * bytesPerSample;
        int chromaH = height / 2;

        long frameDiffBytes = 0;
        int frameMaxDiff = 0;

        // Plane Y
        fixed (byte* pRefY = refFrame.PlaneY)
        {
            for (int y = 0; y < height; y++)
            {
                byte* pHw = hwDownloaded->data[0] + y * hwDownloaded->linesize[0];
                byte* pRef = pRefY + y * rowBytesY;
                for (int b = 0; b < rowBytesY; b++)
                {
                    int diff = Math.Abs(pHw[b] - pRef[b]);
                    if (diff != 0)
                    {
                        frameDiffBytes++;
                        if (diff > frameMaxDiff) frameMaxDiff = diff;
                    }
                }
            }
        }

        // Plane UV (CbCr)
        fixed (byte* pRefUV = refFrame.PlaneUV)
        {
            for (int y = 0; y < chromaH; y++)
            {
                byte* pHw = hwDownloaded->data[1] + y * hwDownloaded->linesize[1];
                byte* pRef = pRefUV + y * rowBytesUV;
                for (int b = 0; b < rowBytesUV; b++)
                {
                    int diff = Math.Abs(pHw[b] - pRef[b]);
                    if (diff != 0)
                    {
                        frameDiffBytes++;
                        if (diff > frameMaxDiff) frameMaxDiff = diff;
                    }
                }
            }
        }

        stats.CuadrosComparados++;
        if (frameDiffBytes == 0)
        {
            stats.CuadrosIdenticos++;
        }
        else
        {
            stats.BytesDistintos += frameDiffBytes;
            if (frameMaxDiff > stats.DiferenciaMaxima) stats.DiferenciaMaxima = frameMaxDiff;
        }
    }

    private static void CompareExactPlanesRaw(
        AVFrame* hwDownloaded,
        AVFrame* srcFrame,
        FormatoExactoInfo stats,
        bool isHevc)
    {
        int width = hwDownloaded->width;
        int height = hwDownloaded->height;
        int bytesPerSample = isHevc ? 2 : 1;
        int rowBytesY = width * bytesPerSample;
        int rowBytesUV = width * bytesPerSample;
        int chromaH = height / 2;

        long frameDiffBytes = 0;
        int frameMaxDiff = 0;

        // Plane Y
        for (int y = 0; y < height; y++)
        {
            byte* pHw = hwDownloaded->data[0] + y * hwDownloaded->linesize[0];
            byte* pSrc = srcFrame->data[0] + y * srcFrame->linesize[0];
            for (int b = 0; b < rowBytesY; b++)
            {
                int diff = Math.Abs(pHw[b] - pSrc[b]);
                if (diff != 0)
                {
                    frameDiffBytes++;
                    if (diff > frameMaxDiff) frameMaxDiff = diff;
                }
            }
        }

        // Plane UV
        for (int y = 0; y < chromaH; y++)
        {
            byte* pHw = hwDownloaded->data[1] + y * hwDownloaded->linesize[1];
            byte* pSrc = srcFrame->data[1] + y * srcFrame->linesize[1];
            for (int b = 0; b < rowBytesUV; b++)
            {
                int diff = Math.Abs(pHw[b] - pSrc[b]);
                if (diff != 0)
                {
                    frameDiffBytes++;
                    if (diff > frameMaxDiff) frameMaxDiff = diff;
                }
            }
        }

        stats.CuadrosComparados++;
        if (frameDiffBytes == 0)
        {
            stats.CuadrosIdenticos++;
        }
        else
        {
            stats.BytesDistintos += frameDiffBytes;
            if (frameMaxDiff > stats.DiferenciaMaxima) stats.DiferenciaMaxima = frameMaxDiff;
        }
    }

    private AVPixelFormat _cachedSwFormat = AVPixelFormat.AV_PIX_FMT_NONE;

    private void ScaleSoftwareFrame(
        AVFrame* swFrame,
        ref SwsContext* sws,
        byte*[] dstData,
        int[] dstLinesize,
        byte[] swRgba)
    {
        if (sws == null || _cachedSwFormat != (AVPixelFormat)swFrame->format)
        {
            if (sws != null) ffmpeg.sws_freeContext(sws);
            _cachedSwFormat = (AVPixelFormat)swFrame->format;
            sws = ffmpeg.sws_getContext(
                swFrame->width, swFrame->height, (AVPixelFormat)swFrame->format,
                VulkanContext.TargetWidth, VulkanContext.TargetHeight, AVPixelFormat.AV_PIX_FMT_RGBA,
                2 /* SWS_BILINEAR */, null, null, null);

            int* inv_table = ffmpeg.sws_getCoefficients((int)ffmpeg.SWS_CS_ITU709);
            int* table = ffmpeg.sws_getCoefficients((int)ffmpeg.SWS_CS_DEFAULT);
            int4 inv = *(int4*)inv_table;
            int4 tbl = *(int4*)table;
            ffmpeg.sws_setColorspaceDetails(sws, in inv, 0, in tbl, 0, 0, 1 << 16, 1 << 16);
        }

        fixed (byte* pSwRgba = swRgba)
        {
            dstData[0] = pSwRgba;
            ffmpeg.sws_scale(sws, swFrame->data, swFrame->linesize, 0, swFrame->height, dstData, dstLinesize);
        }
    }

    private void CompareRgba(
        byte[] gpuRgba,
        byte[] swRgba,
        ref double sumMae,
        ref double sumPsnr,
        bool isHevc = false,
        long pts = 0)
    {
        double mse = 0.0;
        double mae = 0.0;
        int pixels = VulkanContext.TargetWidth * VulkanContext.TargetHeight;
        for (int i = 0; i < pixels; i++)
        {
            int idx = i * 4;
            double dr = Math.Abs(gpuRgba[idx] - swRgba[idx]);
            double dg = Math.Abs(gpuRgba[idx + 1] - swRgba[idx + 1]);
            double db = Math.Abs(gpuRgba[idx + 2] - swRgba[idx + 2]);
            mae += (dr + dg + db) / 3.0;
            mse += (dr * dr + dg * dg + db * db) / 3.0;
        }

        mae /= pixels;
        mse /= pixels;
        double psnr = mse > 0 ? 10.0 * Math.Log10(255.0 * 255.0 / mse) : 99.0;

        VerifiedFramesCount++;
        double mae255 = mae / 255.0;
        sumMae += mae255;
        sumPsnr += psnr;

        if (mae255 > MaeThreshold)
        {
            ThresholdPassed = false;
        }
    }

    private static AVPixelFormat GetFormatVulkan(AVCodecContext* ctx, AVPixelFormat* pix_fmts)
    {
        AVPixelFormat* p = pix_fmts;
        while (*p != AVPixelFormat.AV_PIX_FMT_NONE)
        {
            if (*p == AVPixelFormat.AV_PIX_FMT_VULKAN) return AVPixelFormat.AV_PIX_FMT_VULKAN;
            p++;
        }
        return AVPixelFormat.AV_PIX_FMT_NONE;
    }

    private static double GetProcessMemoryMb()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        using var proc = Process.GetCurrentProcess();
        return proc.WorkingSet64 / (1024.0 * 1024.0);
    }
}
