using System;

namespace Rapcha.Core
{
    /// <summary>
    /// Turns arbitrary capture buffers into clean 100 ms (3200 byte) packets, like
    /// AudioRecorder.kt, and flushes the last partial packet at stop (tailPacket: >= 320 bytes, even length).
    /// </summary>
    public sealed class PcmRechunker
    {
        public const int SampleRate = 16000;
        public const int ChunkSizeBytes = 3200;
        public const int MinTailBytes = 320;

        private readonly byte[] _buffer = new byte[ChunkSizeBytes];
        private int _filled;
        private readonly Action<byte[]> _onChunk;

        public PcmRechunker(Action<byte[]> onChunk) { _onChunk = onChunk; }

        public void Write(byte[] data, int offset, int count)
        {
            while (count > 0)
            {
                int n = Math.Min(count, ChunkSizeBytes - _filled);
                Buffer.BlockCopy(data, offset, _buffer, _filled, n);
                _filled += n; offset += n; count -= n;
                if (_filled == ChunkSizeBytes)
                {
                    var packet = new byte[ChunkSizeBytes];
                    Buffer.BlockCopy(_buffer, 0, packet, 0, ChunkSizeBytes);
                    _filled = 0;
                    _onChunk(packet);
                }
            }
        }

        public void FlushTail()
        {
            int even = _filled - (_filled % 2);
            _filled = 0;
            if (even < MinTailBytes) return;
            var packet = new byte[even];
            Buffer.BlockCopy(_buffer, 0, packet, 0, even);
            _onChunk(packet);
        }
    }
}
