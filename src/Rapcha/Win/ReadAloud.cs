using System;
using Rapcha.Core;

namespace Rapcha.Win
{
    public enum ReadState { Idle, Preparing, Playing, Paused, Finished }

    /// <summary>What the overlay needs from a voice (the Windows voice).</summary>
    public interface IReadAloud : IDisposable
    {
        ReadingPlan Plan { get; }
        int CurrentWord { get; }
        ReadState Status { get; }
        string VoiceName { get; }
        int ProgressEvents { get; }
        event Action Changed;
        void Read(string text, int startWord = 0);
        void TogglePause();
        void Skip(int delta);
        void Stop();
    }
}
