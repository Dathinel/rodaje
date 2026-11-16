using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen.Abstractions;
using FFmpeg.AutoGen.Bindings.DynamicallyLinked;

namespace Rodaje.VulkanVideo;

public static unsafe class ReferenceExtractor
{
    public static Dictionary<long, byte[]> ExtractReferences(string videoPath, int interval = 30)
    {
        var refs = new Dictionary<long, byte[]>();

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

                        refs[frame->pts] = rgba;
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
