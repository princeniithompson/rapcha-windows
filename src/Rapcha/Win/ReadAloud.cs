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
        /// <summary>Creates the engine and opens the audio output now (no sound), so the first read starts at once.</summary>
        void Prime();
        /// <summary>Starts preparing this text's first piece without playing it (a tap that may still turn into a box).</summary>
        void Prepare(string text);
        /// <summary>When the current read's first audio started (UTC); DateTime.MinValue until it has.</summary>
        DateTime AudioStartedAt { get; }
    }
}
