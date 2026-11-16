using System;
using System.Drawing;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Rapcha.Win
{
    /// <summary>
    /// While voice typing, outlines the text field the words will go into with the same blue / purple / green
    /// glow as select to speak. Click-through, never takes focus, ~20 frames per second on four thin strips.
    /// The field is looked up with UI Automation once at the start and then once a second (it may move or the
    /// user may click another field).
    /// </summary>
    public sealed class FieldIndicator : IDisposable
    {
        public const int ThicknessDip = 7;
        private readonly EdgeGlow _glow = new EdgeGlow(ThicknessDip, outward: true, intervalMs: 50);
        private readonly Timer _refresh = new Timer { Interval = 1000 };
        private IntPtr _target;
        private int _session;
        private bool _busy;

        public bool Active => _glow.Active;
        public Rectangle Outline { get; private set; }   // the field rect the glow surrounds (after clamping)
        public FieldInfo Last { get; private set; }

        public FieldIndicator()
        {
            _refresh.Tick += async (s, e) =>
            {
                if (_busy || _target == IntPtr.Zero) return;
                int session = _session;
                var info = await DetectAsync(_target);
                if (session != _session || _target == IntPtr.Zero) return;
                if (info.Editable) Show(info); else Hide(keepTarget: true);
            };
        }

        /// <summary>Looks up the focused field in the target window (at most ~800 ms).</summary>
        public async Task<FieldInfo> DetectAsync(IntPtr target)
        {
            _busy = true;
            try
            {
                var why = new StringBuilder();
                var info = await Uia.FocusedFieldAsync(target, 800, why) ?? new FieldInfo { Reason = "uia_timeout" + why };
                Last = info;
                return info;
            }
            finally { _busy = false; }
        }

        /// <summary>Starts following the target window's focused field; returns what was found first.</summary>
        public async Task<FieldInfo> BeginAsync(IntPtr target)
        {
            Hide();
            int session = ++_session;
            _target = target;
            var info = await DetectAsync(target);
            if (session != _session) return info;
            if (info.Editable) Show(info);
            _refresh.Start();
            return info;
        }

        private void Show(FieldInfo info)
        {
            var r = Clamp(info.Bounds, _target);
            if (r.Width < 6 || r.Height < 6) { Hide(keepTarget: true); return; }
            if (_glow.Active && r == Outline) return;
            _glow.Hide();
            Outline = r;
            _glow.Show(r, null);
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
            // Leave room for the glow inside the screen.
            int t = (int)Math.Round(ThicknessDip * Native.DpiAt(r.X + r.Width / 2, r.Y + r.Height / 2) / 96.0);
            var inner = Rectangle.FromLTRB(screen.Left + t, screen.Top + t, screen.Right - t, screen.Bottom - t);
            r.Intersect(inner);
            return r.Width > 0 && r.Height > 0 ? r : Rectangle.Empty;
        }

        public void Hide() => Hide(false);

        private void Hide(bool keepTarget)
        {
            if (!keepTarget) { _refresh.Stop(); _target = IntPtr.Zero; _session++; }
            _glow.Hide();
            Outline = Rectangle.Empty;
        }

        public void Dispose()
        {
            Hide();
            _refresh.Dispose();
            _glow.Dispose();
        }
    }
}
