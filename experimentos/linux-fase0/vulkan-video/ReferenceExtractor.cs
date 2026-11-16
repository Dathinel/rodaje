using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;

namespace Rodaje.VulkanVideo;

public sealed class ReferenceFrame
{
    public long Pts { get; init; }
    public byte[] Rgba { get; init; } = Array.Empty<byte>();
    public byte[] PlaneY { get; init; } = Array.Empty<byte>();
    public byte[] PlaneUV { get; init; } = Array.Empty<byte>();
}

public static unsafe class ReferenceExtractor
{
    public static Dictionary<long, ReferenceFrame> ExtractReferences(string videoPath, bool isHevc, int interval = 30)
    {
        var refs = new Dictionary<long, ReferenceFrame>();

        AVFormatContext* fmtCtx = ffmpeg.avformat_alloc_context();
        if (ffmpeg.avformat_open_input(&fmtCtx, videoPath, null, null) < 0) return refs;
        if (ffmpeg.avformat_find_stream_info(fmtCtx, null) < 0)
        {
            ffmpeg.avformat_close_input(&fmtCtx);
            return refs;
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
            return refs;
        }

        AVCodecParameters* codecpar = fmtCtx->streams[videoStream]->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id);
        AVCodecContext* cctx = ffmpeg.avcodec_alloc_context3(codec);
        ffmpeg.avcodec_parameters_to_context(cctx, codecpar);
        ffmpeg.avcodec_open2(cctx, codec, null);

        SwsContext* sws = null;
        AVPacket* pkt = ffmpeg.av_packet_alloc();
        AVFrame* frame = ffmpeg.av_frame_alloc();

        byte*[] dstData = new byte*[4];
        int[] dstLinesize = new int[] { VulkanContext.TargetWidth * 4, 0, 0, 0 };

        int frameIdx = 0;
        while (ffmpeg.av_read_frame(fmtCtx, pkt) >= 0)
        {
            if (pkt->stream_index == videoStream)
            {
                ffmpeg.avcodec_send_packet(cctx, pkt);
                while (ffmpeg.avcodec_receive_frame(cctx, frame) == 0)
                {
                    if (frameIdx % interval == 0)
                    {
                        // 1. Convert to RGBA for quality verification (MAE/PSNR)
                        if (sws == null)
                        {
                            sws = ffmpeg.sws_getContext(
                                frame->width, frame->height, (AVPixelFormat)frame->format,
                                VulkanContext.TargetWidth, VulkanContext.TargetHeight, AVPixelFormat.AV_PIX_FMT_RGBA,
                                2 /* SWS_BILINEAR */, null, null, null);

                            int* inv_table = ffmpeg.sws_getCoefficients((int)ffmpeg.SWS_CS_ITU709);
                            int* table = ffmpeg.sws_getCoefficients((int)ffmpeg.SWS_CS_DEFAULT);
                            int4 inv = *(int4*)inv_table;
                            int4 tbl = *(int4*)table;
                            ffmpeg.sws_setColorspaceDetails(sws, in inv, 0, in tbl, 0, 0, 1 << 16, 1 << 16);
                        }

                        byte[] rgba = new byte[VulkanContext.TargetBufferSize];
                        fixed (byte* pRgba = rgba)
                        {
                            dstData[0] = pRgba;
                            ffmpeg.sws_scale(sws, frame->data, frame->linesize, 0, frame->height, dstData, dstLinesize);
                        }

                        // 2. Extract exact reference planes (NV12 or P010 format)
                        int width = frame->width;
                        int height = frame->height;
                        int chromaH = height / 2;
                        int chromaW = width / 2;

                        byte[] planeY;
                        byte[] planeUV;

                        if (!isHevc)
                        {
                            // H.264: YUV420P -> NV12
                            planeY = new byte[width * height];
                            planeUV = new byte[width * chromaH];

                            fixed (byte* pDstY = planeY, pDstUV = planeUV)
                            {
                                // Plane Y
                                for (int y = 0; y < height; y++)
                                {
                                    Buffer.MemoryCopy(frame->data[0] + y * frame->linesize[0], pDstY + y * width, width, width);
                                }

                                // Plane UV (interleave Cb and Cr)
                                for (int y = 0; y < chromaH; y++)
                                {
                                    byte* pSrcU = frame->data[1] + y * frame->linesize[1];
                                    byte* pSrcV = frame->data[2] + y * frame->linesize[2];
                                    byte* pRowDst = pDstUV + y * width;
                                    for (int x = 0; x < chromaW; x++)
                                    {
                                        pRowDst[2 * x] = pSrcU[x];
                                        pRowDst[2 * x + 1] = pSrcV[x];
                                    }
                                }
                            }
                        }
                        else
                        {
                            // HEVC: YUV420P10LE -> P010LE (10-bit in MSB: << 6)
                            planeY = new byte[width * 2 * height];
                            planeUV = new byte[width * 2 * chromaH];

                            fixed (byte* pDstY = planeY, pDstUV = planeUV)
                            {
                                ushort* pDstShortY = (ushort*)pDstY;
                                ushort* pDstShortUV = (ushort*)pDstUV;

                                // Plane Y
                                for (int y = 0; y < height; y++)
                                {
                                    ushort* pSrcY = (ushort*)(frame->data[0] + y * frame->linesize[0]);
                                    ushort* pRowDst = pDstShortY + y * width;
                                    for (int x = 0; x < width; x++)
                                    {
                                        pRowDst[x] = (ushort)(pSrcY[x] << 6);
                                    }
                                }

                                // Plane UV (interleave and shift by 6)
                                for (int y = 0; y < chromaH; y++)
                                {
                                    ushort* pSrcU = (ushort*)(frame->data[1] + y * frame->linesize[1]);
                                    ushort* pSrcV = (ushort*)(frame->data[2] + y * frame->linesize[2]);
                                    ushort* pRowDst = pDstShortUV + y * width;
                                    for (int x = 0; x < chromaW; x++)
                                    {
                                        pRowDst[2 * x] = (ushort)(pSrcU[x] << 6);
                                        pRowDst[2 * x + 1] = (ushort)(pSrcV[x] << 6);
                                    }
                                }
                            }
                        }

                        refs[frame->pts] = new ReferenceFrame
                        {
                            Pts = frame->pts,
                            Rgba = rgba,
                            PlaneY = planeY,
                            PlaneUV = planeUV
                        };
                    }
                    frameIdx++;
                    ffmpeg.av_frame_unref(frame);
                }
            }
            ffmpeg.av_packet_unref(pkt);
        }

        if (sws != null) ffmpeg.sws_freeContext(sws);
        ffmpeg.av_frame_free(&frame);
        ffmpeg.av_packet_free(&pkt);
        ffmpeg.avcodec_free_context(&cctx);
        ffmpeg.avformat_close_input(&fmtCtx);

        return refs;
    }
}
