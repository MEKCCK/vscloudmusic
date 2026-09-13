using System;
using System.Collections.Generic;
using OpenTK.Audio.OpenAL;
using Vintagestory.API.Common;
using WyyPlayer.Core.Audio;

namespace WyyPlayer.Audio
{
    /// <summary>QueueChunkSafe 的语义化结果，取代原先把三种失败压成一个 bool 的做法。</summary>
    public enum QueueChunkResult
    {
        /// <summary>成功解码并入队一块。</summary>
        Queued,
        /// <summary>队列已满，下一 tick 重试。</summary>
        QueueFull,
        /// <summary>解码器报告流结束（本块未入队）。</summary>
        EndOfStream,
        /// <summary>AL 操作失败（例如 GenBuffer 失败），本块未消费，可重试。</summary>
        Error,
    }

    /// <summary>
    /// 在游戏已有的 OpenAL 上下文上做缓冲队列流式播放。
    /// 参照 AllMusic 的做法：不创建设备/上下文，只生成 source 与 buffer。
    /// 非线程安全 —— 所有 AL 调用必须来自同一线程。
    /// </summary>
    public sealed class OpenAlStreamer : IDisposable
    {
        private readonly int _source;
        private readonly int _sampleRate;
        private readonly int _channels;
        private readonly int _queueSize;


        private readonly ILogger _log;
        // 本地 ID 集合只负责「删除」；队列长度一律以 AL 的 BuffersQueued 为准（避免计数漂移）。
        // 每次观察后与 AL 的真实队列数对齐（ResyncLocalQueue）。
        /// <summary>显式暂停标记。与 AL 源状态分开记：源可能因「队列暂时排空」而
        /// 自转为 STOPPED，光看状态区分不出「暂停中」与「恰好没数据了」。</summary>
        private bool _paused;

        private readonly Queue<int> _queued = new();
        // 与 _queued 平行的「每块字节数」队列，仅用于估算已播时长（进度显示）。
        // 估算不精确是允许的：VBR 下字节↔时长的换算本来就有偏差，进度条不要求帧级精确。
        private readonly Queue<int> _queuedBytes = new();
        private long _playedBytes;
        private bool _disposed;

        public OpenAlStreamer(int sampleRate, int channels, ILogger log, int queueSize = 16)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            _queueSize = queueSize;
            _log = log ?? throw new ArgumentNullException(nameof(log));

            _source = AL.GenSource();

            if (_source == 0)
                throw new InvalidOperationException(
                    $"AL.GenSource 失败，AL 错误: {AL.GetError()}");
        }

        public bool IsPlaying =>
            !_disposed && State == ALSourceState.Playing;

        /// <summary>
        /// AL 权威：以源的 BuffersQueued 为真实队列长度。
        /// 本地 ID 集合只用于删除；与 AL 计数不一致时按 AL 对齐（ResyncLocalQueue）。
        /// </summary>
        public int QueuedCount
        {
            get
            {
                if (_disposed) return 0;
                int alQueued = Math.Max(0, AL.GetSource(_source, ALGetSourcei.BuffersQueued));
                ResyncLocalQueue(alQueued);
                return alQueued;
            }
        }

        private ALSourceState State =>
            (ALSourceState)AL.GetSource(_source, ALGetSourcei.SourceState);

        private ALFormat Format =>
            _channels == 1 ? ALFormat.Mono16 : ALFormat.Stereo16;

        /// <summary>解码一块 PCM 并尝试入队。结果为四种语义之一，绝不吞块、绝不以「失败」冒充「流结束」。</summary>
        public QueueChunkResult QueueChunkSafe(Mp3Decoder decoder, byte[] buffer)
        {
            if (_disposed || decoder == null) return QueueChunkResult.Error;
            if (QueuedCount >= _queueSize) return QueueChunkResult.QueueFull;

            // 先分配 AL 缓冲：若失败，立即返回错误，绝不消费解码块（修复：原来在
            // TryReadChunk 之后 GenBuffer==0 会静默丢掉已经解码出来的那一块）。
            int bufferId = AL.GenBuffer();
            if (bufferId == 0)
            {
                var err = AL.GetError();
                _log.Error($"[vscloudmusic] AL.GenBuffer 失败，AL 错误: {err}，未消费解码块");
                return QueueChunkResult.Error;
            }

            // 缓冲已分配但流已结束：删掉刚分配的空缓冲，报告「结束」而非「失败」，
            // 调用方不会把它当成 EOF 以外的错误而重试丢块。
            if (!decoder.TryReadChunk(buffer, out var chunk))
            {
                AL.DeleteBuffer(bufferId);
                return QueueChunkResult.EndOfStream;
            }

            // BufferData 的 span 重载按 span 长度取数据，因此必须切片到有效长度
            AL.BufferData(bufferId, Format, chunk.Data.AsSpan(0, chunk.Count), _sampleRate);
            AL.SourceQueueBuffer(_source, bufferId);
            _queued.Enqueue(bufferId);
            _queuedBytes.Enqueue(chunk.Count);

            // 首块入队时自行开播；但**绝不在显式暂停后自行恢复** —— 否则暂停期间
            // 任何一次入队（例如刚暂停时队列恰好还有空位）都会把源重新点亮，
            // 表现为「暂停了却还有声音」或「暂停后自己继续放」。
            if (!_paused && State != ALSourceState.Playing)
                AL.SourcePlay(_source);

            return QueueChunkResult.Queued;
        }

        /// <summary>回收已播放完的缓冲，释放队列空间。</summary>
        public void Pump()
        {
            if (_disposed) return;

            int processed = AL.GetSource(_source, ALGetSourcei.BuffersProcessed);
            if (processed < 0) processed = 0;
            // 防御：AL 报告的 processed 不可能超过本地已入队的数量；若上下文数据错乱，收紧防死循环。
            if (processed > _queued.Count) processed = _queued.Count;

            while (processed-- > 0)
            {
                int bufferId = AL.SourceUnqueueBuffer(_source);
                if (bufferId == 0)
                {
                    // 0 绝不能当作「无事发生」：读取并清除 AL 错误、记录日志，然后跳出排空循环。
                    // 不 Dequeue、不 DeleteBuffer —— AL 队列没有变化，本地集合保持一致；
                    // 错误状态被 GetError 消费后，后续 tick 的调用不再被同一个错误污染。
                    ALError err = AL.GetError();
                    _log.Error(
                        $"[vscloudmusic] AL.SourceUnqueueBuffer 返回 0（AL 错误: {err}），" +
                        "中止本次排空，下一 tick 重试");
                    break;
                }
                if (_queued.Count > 0) _queued.Dequeue();
                // 该块已被播放完毕：计入已播字节，用于进度估算。
                if (_queuedBytes.Count > 0) _playedBytes += _queuedBytes.Dequeue();
                AL.DeleteBuffer(bufferId);
            }

            // 排空（或异常中断）后，强制与 AL 的真实队列数对齐；本地集合只负责删除。
            ResyncLocalQueue(Math.Max(0, AL.GetSource(_source, ALGetSourcei.BuffersQueued)));
        }

        /// <summary>累计已播出的 PCM 字节数（用于估算进度）。新一首歌开始时由调用方重置。</summary>
        public long PlayedBytes => _playedBytes;

        /// <summary>把已播字节计数归零（切换曲目时调用）。</summary>
        public void ResetProgress() => _playedBytes = 0;

        /// <summary>
        /// 暂停播放。与 <see cref="Stop"/> 的关键区别：**保留缓冲队列**，只让源停止消费。
        /// Stop() 会清空并删除整个队列，那是「停止」而不是「暂停」——
        /// 用 Stop 实现暂停会导致「继续」时从头重排，不是从原处继续。
        /// </summary>
        public void Pause()
        {
            if (_disposed) return;
            _paused = true;
            AL.SourcePause(_source);
        }

        /// <summary>从暂停处继续，不重排队列。</summary>
        public void Resume()
        {
            if (_disposed) return;
            _paused = false;
            if (State != ALSourceState.Playing) AL.SourcePlay(_source);
        }

        public void SetVolume(float gain)
        {
            if (_disposed) return;
            AL.Source(_source, ALSourcef.Gain, Math.Clamp(gain, 0f, 1f));
        }

        public void Stop()
        {
            if (_disposed) return;
            // 清掉暂停标记：停止是「彻底结束」，残留的 _paused 会让下次入队不再自行开播。
            _paused = false;
            AL.SourceStop(_source);

            // OpenAL 1.1 释放顺序：源停止后必须先解除队列中的缓冲，然后才能删除缓冲、
            // 最后才清空源的缓冲槽位 —— 队列非空时 AL.Source(..., Buffer, 0) 与
            // AL.DeleteBuffer 都是非法操作，会在游戏共享的 OpenAL 上下文上留下粘性错误。
            while (_queued.Count > 0)
            {
                int bufferId = AL.SourceUnqueueBuffer(_source);
                if (bufferId == 0)
                {
                    // 与 Pump 一致：0 绝不是「无事发生」，读取并消费 AL 错误后停止解除。
                    ALError err = AL.GetError();
                    _log.Error(
                        $"[vscloudmusic] 停止时 AL.SourceUnqueueBuffer 返回 0（AL 错误: {err}），" +
                        "终止解除队列");
                    break;
                }
                _queued.Dequeue();
            }

            // 删除已解除的缓冲：本地集合与 AL 已对齐（成功解除到空），剩余 ID 仅属
            // 解除被中断的异常路径，一并删掉不会触碰仍在源上的缓冲（源即将删除）。
            while (_queued.Count > 0)
                AL.DeleteBuffer(_queued.Dequeue());
            // 字节队列随之清空：停止后这些块不会再被播放，不得计入进度。
            _queuedBytes.Clear();

            // 队列已空，此时清空缓冲槽位才是合法操作。
            AL.Source(_source, ALSourcei.Buffer, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            Stop();
            if (AL.IsSource(_source)) AL.DeleteSource(_source);
            _disposed = true;
        }

        /// <summary>
        /// 本地 ID 集合与 AL 的真实队列数对齐：本地偏多时，多余的 ID 已不在源上，释放之；
        /// 本地偏少（理论上不可达）时无法凭空恢复 ID，仅告警并保持 AL 侧计数为权威。
        /// </summary>
        private void ResyncLocalQueue(int alQueued)
        {
            if (_queued.Count == alQueued) return;

            while (_queued.Count > alQueued)
            {
                int stale = _queued.Dequeue();
                // 对应的字节数一并丢弃：这些块从未被播放，不得计入进度。
                if (_queuedBytes.Count > 0) _queuedBytes.Dequeue();
                // IsBuffer 防御：若 AL 已不认识该 ID，DeleteBuffer 只会产生噪音错误。
                if (AL.IsBuffer(stale)) AL.DeleteBuffer(stale);
            }

            if (_queued.Count < alQueued)
                _log.Warning(
                    $"[vscloudmusic] 本地缓冲集合({_queued.Count})少于 AL 队列({alQueued})，" +
                    "ID 不可恢复，等待后续排空再对齐");
        }
    }
}
