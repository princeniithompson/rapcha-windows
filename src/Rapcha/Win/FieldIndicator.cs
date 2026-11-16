using System;
using System.Drawing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// Follows keyboard focus while voice typing: a rounded outline in the user's glow colour around the real
    /// text field that has focus (and a caret), moved when the user clicks into another field, hidden when focus
    /// leaves text. <see cref="Target"/> is the window the words will be pasted into, or zero (clipboard only).
    /// Focus is re-checked on every UIA focus-changed event and every 300 ms. The same focus events also tell
    /// the app when focus enters a text field while idle (to wake the resting ring).
    /// </summary>
    public sealed class FieldIndicator : IDisposable
    {
        public const int ThicknessDip = FieldGlow.GlowDip + 2;
        public const int PollMs = 300;
        private readonly FieldGlow _glow = new FieldGlow(50);
        private readonly System.Windows.Forms.Timer _poll = new System.Windows.Forms.Timer { Interval = PollMs };
        private readonly SynchronizationContext _ui;
        private int _busy;              // a lookup is running
        private int _focusPending;      // a focus event is waiting to be handled
        private int _session;

        public bool Tracking { get; private set; }
        public bool Active => _glow.Active;
        public Rectangle Outline { get; private set; }
        public FieldInfo Last { get; private set; }
        /// <summary>True: a valid field has focus; false: none; null: not known yet.</summary>
        public bool? HasField { get; private set; }
        /// <summary>The window of the focused text field (the paste target), or zero.</summary>
        public IntPtr Target { get; private set; }
        public int Checks { get; private set; }
        public int Moves { get; private set; }
        /// <summary>A lookup is running right now.</summary>
        public bool Busy => Volatile.Read(ref _busy) == 1;
        public FieldGlow Glow => _glow;
        public Color Color { get => _glow.Color; set => _glow.Color = value; }

        /// <summary>The field / target changed while tracking (UI thread).</summary>
        public event Action<FieldInfo> Changed;
        /// <summary>Focus landed in a valid text field (any time; UI thread). Only checked when WantFocusChecks says so.</summary>
        public event Action FocusEnteredField;
        /// <summary>Whether idle focus events should be evaluated (e.g. only while the ring rests).</summary>
        public Func<bool> WantFocusChecks;

        public FieldIndicator()
        {
            _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
            _poll.Tick += async (s, e) => { if (Tracking) await CheckNowAsync(); };
        }

        /// <summary>Starts listening to UIA focus events (once, at app start).</summary>
        public void EnableFocusEvents()
        {
            Uia.StartFocusEvents(() =>
            {
                if (Interlocked.Exchange(ref _focusPending, 1) == 1) return;
                _ui.Post(async _ =>
                {
                    await Task.Delay(60); // let the app finish moving focus (and its caret)
                    Interlocked.Exchange(ref _focusPending, 0);
                    await OnFocusEventAsync();
                }, null);
            });
        }

        /// <summary>Focus moved somewhere (any UIA focus event, UI thread).</summary>
        public event Action FocusMoved;

        /// <summary>One lookup of the focused element, without changing the outline. Null if UIA is busy or slow.</summary>
        public Task<FieldInfo> ProbeAsync() => LookupAsync();

        private async Task OnFocusEventAsync()
        {
            FocusMoved?.Invoke();
            if (Tracking) { await CheckNowAsync(); return; }
            if (WantFocusChecks == null || !WantFocusChecks()) return;
            var info = await LookupAsync();
            if (info != null && info.Editable) FocusEnteredField?.Invoke();
        }

        private async Task<FieldInfo> LookupAsync()
        {
            if (Interlocked.Exchange(ref _busy, 1) == 1) return null; // the previous lookup is still running (a slow app)
            try
            {
                var why = new StringBuilder();
                var info = await Uia.FocusedFieldAsync(Uia.WarmedUp ? 1000 : 3000, why);
                Checks++;
                if (info == null) { Last = new FieldInfo { Reason = "uia_timeout" + why }; return null; }
                return info;
            }
            finally { Interlocked.Exchange(ref _busy, 0); }
        }

        /// <summary>Starts following focus (voice typing started). <paramref name="provisional"/> is used until the first lookup answers.</summary>
        public void Start(IntPtr provisional)
        {
            _session++;
            Tracking = true;
            HasField = null;
            Target = provisional;
            Moves = 0;
            _poll.Start();
            _ = CheckNowAsync();
        }

        /// <summary>Stops following focus and hides the outline (the last decision stays readable).</summary>
        public void Stop()
        {
            Tracking = false;
            _poll.Stop();
            _session++;
            HideOutline();
        }

        public void Hide() => Stop();

        /// <summary>One lookup now; applies it while tracking. Returns null if UIA did not answer (state kept).</summary>
        public async Task<FieldInfo> CheckNowAsync()
        {
            int session = _session;
            var info = await LookupAsync();
            if (info == null) return null;
            if (session != _session && !Tracking) return info;
            Last = info;
            if (!Tracking) return info;
            if (info.OwnApp) return info;            // clicks on Rapcha's own windows keep the field
            Apply(info);
            return info;
        }

        private void Apply(FieldInfo info)
        {
            bool before = HasField == true;
            var oldTarget = Target;
            var oldOutline = Outline;
            if (info.Editable)
            {
                HasField = true;
                Target = info.Window;
                var r = Clamp(info.Bounds, info.Window);
                if (r.Width >= 6 && r.Height >= 6)
                {
                    if (!_glow.Active || r != Outline) { Outline = r; _glow.Show(r); }
                }
                else HideOutline();
            }
            else
            {
                HasField = false;
                Target = IntPtr.Zero;
                HideOutline();
            }
            bool changed = before != (HasField == true) || oldTarget != Target || oldOutline != Outline;
            if (changed && before && HasField == true) Moves++;
            if (changed) Changed?.Invoke(info);
        }

        private void HideOutline()
        {
            _glow.Hide();
            Outline = Rectangle.Empty;
        }

        /// <summary>
        /// Keeps the outline on the visible part of the window and screen: a field that scrolls off screen, or a
        /// whole document bigger than the window, is cut to what you can see (a few pixels in, so the glow shows).
        /// </summary>
        public static Rectangle Clamp(Rectangle field, IntPtr window)
        {
            if (field.Width <= 0 || field.Height <= 0) return Rectangle.Empty;
            var r = field;
            if (window != IntPtr.Zero && Native.GetWindowRect(Native.GetAncestor(window, Native.GA_ROOT), out var w))
            {
                var wr = Rectangle.FromLTRB(w.Left, w.Top, w.Right, w.Bottom);
                if (wr.Width > 0 && wr.Height > 0) r.Intersect(wr);
            }
            var screen = Screen.FromRectangle(field.Width > 0 ? field : r).Bounds;
            r.Intersect(screen);
            if (r.Width <= 0 || r.Height <= 0) return Rectangle.Empty;
            int t = (int)Math.Round(ThicknessDip * Native.DpiAt(r.X + r.Width / 2, r.Y + r.Height / 2) / 96.0);
            var inner = Rectangle.FromLTRB(screen.Left + t, screen.Top + t, screen.Right - t, screen.Bottom - t);
            r.Intersect(inner);
            return r.Width > 0 && r.Height > 0 ? r : Rectangle.Empty;
        }

        public void Dispose()
        {
            Stop();
            Uia.StopFocusEvents();
            _poll.Dispose();
            _glow.Dispose();
        }
    }
}
