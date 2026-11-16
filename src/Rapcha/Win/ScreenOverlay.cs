using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using Rapcha.Core;

namespace Rapcha.Win
{
    /// <summary>
    /// Select to speak, modelled on Android's Select to Speak with Circle to Search: the monitor is frozen
    /// as a slightly darkened screenshot, the edges glow, and the user draws a bright green box around text
    /// (drag, or click two corners) or taps a word to read its line. Text comes from the built-in Windows OCR,
    /// so exactly what is visible is read. While reading, a green box moves from word to word.
    /// </summary>
    public sealed class ScreenOverlay : Form
    {
        public const string NoTextThere = "No text found there.";

        public enum Control { None, Back, PlayPause, Forward, Stop, Close }

        private static readonly Color Green = Color.FromArgb(70, 235, 120);

        private readonly Bitmap _dark;
        private readonly int _dpi;
        private readonly BoxSelector _selector;
        private readonly System.Windows.Forms.Timer _anim = new System.Windows.Forms.Timer { Interval = 33 };
        private readonly System.Windows.Forms.Timer _tapTimer = new System.Windows.Forms.Timer { Interval = 15 };
        private readonly DateTime _born = DateTime.UtcNow;
        private readonly bool _silentVoice;
        private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();
        /// <summary>Monotonic milliseconds for the gesture state machine.</summary>
        internal static long Now() => Clock.ElapsedMilliseconds;

        private OcrService.Result _ocr;
        private string _status = "";
        private IReadAloud _reader;
        private SpokenText _spoken;
        private Box _hl, _hlFrom, _hlTarget;
        private DateTime _hlMoved;
        private bool _hlVisible;
        private Rectangle _lastBoxRect, _lastHlRect;
        private (double x, double y)? _pendingTap;
        private Box? _pendingBox;
        private readonly Dictionary<Control, Rectangle> _controls = new Dictionary<Control, Rectangle>();
        private Control _pressed = Control.None;
        private Font _font, _small;

        // The snapped text is outlined (numbered when there are several blocks) the moment reading starts; the
        // outlines soften after this long. Reading never waits for them.
        public const int PreviewMs = 450;
        public const int TapPatchBudgetMs = 1500;
        private List<SnapPart> _preview;
        private bool _previewStrong;
        private readonly System.Windows.Forms.Timer _previewTimer = new System.Windows.Forms.Timer { Interval = PreviewMs };
        /// <summary>A selection is waiting for its text (the first look at the screen, a closer look around a tap, or a pending tap).</summary>
        public bool Previewing => _preparing > 0 || _pendingBox.HasValue || _pendingTap.HasValue || _selector.Current == BoxSelector.State.TapPending;
        private int _preparing;
        public int PreviewParts { get; private set; }

        public string LastReadText { get; private set; } = "";
        /// <summary>The OCR words of the last read, in order (the screen's own text, before code is made sayable).</summary>
        public List<OcrWord> LastReadWords { get; private set; } = new List<OcrWord>();
        /// <summary>Release of the box (or of the tap's click) to the first audio, ms; -1 until it happens.</summary>
        public int FirstWordMs { get; private set; } = -1;
        /// <summary>Release to the words being handed to the voice (snap done), ms.</summary>
        public int WordsReadyMs { get; private set; } = -1;
        /// <summary>The gesture of the last read: "box" (drag), "corners" (two clicks), "tap".</summary>
        public string LastMode { get; private set; } = "";
        /// <summary>What the last read used: "index" (complete), "partial", "fast" (first look only), "quick" (its own passes), "patch".</summary>
        public string LastIndexState { get; private set; } = "";
        public int RegionPassesUsed { get; private set; }
        public int TapPatches { get; private set; }
        public int TapsPrepared { get; private set; }
        /// <summary>Selections started (a box released or a tap read), for self-tests.</summary>
        public int ReadCount { get; private set; }
        public FrameSource Source { get; }
        private DateTime _releaseAt;
        private bool _waitFirstWord;
        private int _selGen;
        public int HighlightMoves { get; private set; }
        public IReadAloud Reader => _reader;
        public OcrService.Result Ocr => _ocr;
        public BoxSelector.State GestureState => _selector.Current;

        // ---- the background index
        private System.Threading.Thread _indexThread;
        private volatile bool _closing;
        private long _hotUntil;              // Now() ms: the index pauses until then (a selection is being read)
        private readonly DateTime _openedAt = DateTime.UtcNow;
        private int _indexJobs, _indexDone;
        /// <summary>Every background pass is merged into the index.</summary>
        public bool IndexReady { get; private set; }
        /// <summary>Overlay open to the first look (fast pass) being usable, ms.</summary>
        public int FastPassMs { get; private set; } = -1;
        /// <summary>Overlay open to the whole index being ready, ms.</summary>
        public int IndexMs { get; private set; } = -1;
        private SnapPart _tapPrep;
        private string _tapPrepText;

        public event Action<Control> ControlUsed;

        public ScreenOverlay(Rectangle bounds, Bitmap screenshot, int dpi, bool silentVoice)
        {
            _dpi = dpi;
            _silentVoice = silentVoice;
            Source = FrameSource.From(screenshot); // physical pixels, for the OCR index
            _selector = new BoxSelector(BoxSelector.MoveThresholdPx * dpi / 96.0);
            Voices.WarmUp();                        // once per process: loads the voice data off the UI thread
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            TopMost = true;
            AutoScaleMode = AutoScaleMode.None;
            KeyPreview = true;
            Text = "Rapcha select to speak";
            Bounds = bounds;
            Cursor = Cursors.Cross;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);

            // The frozen screen, slightly darkened (kept in premultiplied format for fast drawing).
            _dark = new Bitmap(screenshot.Width, screenshot.Height, PixelFormat.Format32bppPArgb);
            using (var g = Graphics.FromImage(_dark))
            {
                g.DrawImageUnscaled(screenshot, 0, 0);
                using (var shade = new SolidBrush(Color.FromArgb(72, 8, 10, 20))) g.FillRectangle(shade, 0, 0, _dark.Width, _dark.Height);
            }
            _font = new Font("Segoe UI", S(15), FontStyle.Regular, GraphicsUnit.Pixel);
            _small = new Font("Segoe UI Semibold", S(13), FontStyle.Regular, GraphicsUnit.Pixel);
            _anim.Tick += (s, e) => Animate();
            _previewTimer.Tick += (s, e) =>
            {
                _previewTimer.Stop();
                _previewStrong = false;
                InvalidatePreview();
            };
            _tapTimer.Tick += (s, e) =>
            {
                if (_selector.Current != BoxSelector.State.TapPending) { _tapTimer.Stop(); return; }
                var r = _selector.Tick(Now());
                if (r.Kind != BoxSelector.Kind.None) { _tapTimer.Stop(); Selection(r); }
            };
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            // The voice is created once for the app and kept; make sure it is primed (engine + audio output open)
            // while the user positions the pointer.
            // (After the first paint: a cold engine may take a moment and must not delay the overlay appearing.)
            System.Threading.Tasks.Task.Delay(60).ContinueWith(_ => OnUi(() => { if (!IsDisposed) Voices.PrimeShared(_silentVoice); }));
        }

        private float S(double dip) => (float)(dip * _dpi / 96.0);
        private int Si(double dip) => (int)Math.Round(dip * _dpi / 96.0);

        // ---------------- The screen index ----------------
        //
        // On open: one fast colour pass over the whole frozen screen (usually well under a second), then the other
        // passes one at a time on a low-priority thread, each merged into lines and blocks off the UI thread and
        // swapped in. A box or tap reads from whatever index is there at the release: no OCR on the hot path.

        /// <summary>Starts the index (the app calls this right after showing the overlay).</summary>
        public void StartIndex()
        {
            OcrService.FastAsync(Source, _dpi).ContinueWith(t =>
            {
                var r = t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion ? t.Result : new OcrService.Result { Problem = "Couldn't read the screen. Try again." };
                OnUi(() => SetOcr(r));
            });
        }

        public void SetOcr(OcrService.Result r)
        {
            bool first = _ocr == null;
            _ocr = r;
            if (first)
            {
                FastPassMs = (int)(DateTime.UtcNow - _openedAt).TotalMilliseconds;
                AppStorage.Log("Overlay index: first look open_to_ready_ms=" + FastPassMs + " ocr_ms=" + r.Ms + " passes=" + r.Passes.Count +
                               " words=" + r.Page.WordCount + " lines=" + r.Page.Lines.Count + " blocks=" + r.Page.Blocks.Count + (r.Problem.Length > 0 ? " problem=yes" : ""));
            }
            if (r.Problem.Length > 0) _status = r.Problem;
            else if (r.Page.WordCount == 0) _status = "No text found on this screen.";
            else _status = "";
            InvalidateBottom();
            if (r.Problem.Length == 0) StartBackgroundIndex();
            // A selection made before the first look was back.
            if (_pendingBox.HasValue) { var b = _pendingBox.Value; _pendingBox = null; ReadBoxNow(b, "fast"); }
            else if (_pendingTap.HasValue) { var (x, y) = _pendingTap.Value; _pendingTap = null; ReadTapNow(x, y); }
        }

        private void StartBackgroundIndex()
        {
            if (_indexThread != null || _ocr == null) return;
            var have = new HashSet<string>(_ocr.Passes.Select(p => p.Name));
            PageLayout latest = _ocr.Page;
            var jobs = OcrService.IndexJobs(Source, _dpi, YieldToReading, () => _hotCts.Token, () => latest).Where(j => !have.Contains(j.name)).ToList();
            _indexJobs = jobs.Count;
            if (jobs.Count == 0) { IndexReady = true; IndexMs = (int)(DateTime.UtcNow - _openedAt).TotalMilliseconds; return; }
            var basePasses = _ocr.Passes.ToList();
            string lang = _ocr.Language;
            double scale = _ocr.Scale;
            int firstMs = _ocr.Ms;
            _indexThread = new System.Threading.Thread(() =>
            {
                var all = new List<OcrPass>(basePasses);
                foreach (var job in jobs)
                {
                    if (!SafeYield()) return;
                    OcrPass p = null;
                    try { p = job.run(); }
                    catch (System.Threading.ThreadInterruptedException) { return; }
                    catch (Exception e) { AppStorage.Log("Overlay index: pass failed " + e.GetType().Name); }
                    if (_closing) return;
                    OcrService.Result r = null;
                    if (p != null)
                    {
                        all.Add(p);
                        r = new OcrService.Result { Language = lang, Scale = scale, Ms = firstMs };
                        r.Passes.AddRange(all);
                        if (!SafeYield()) return;
                        var t0 = DateTime.UtcNow;
                        OcrService.Rebuild(r);   // merge + lines + blocks, off the UI thread
                        p.MergeMs = (int)(DateTime.UtcNow - t0).TotalMilliseconds;
                        if (r.Problem.Length == 0) latest = r.Page;
                    }
                    var name = job.name;
                    OnUi(() => IndexStep(r, name, p));
                }
            }) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Rapcha OCR index" };
            _indexThread.Start();
        }

        /// <summary>
        /// Called by the background index before every tile: it waits while a selection is starting (so the voice
        /// starts without competing for the CPU), and while speech plays it leaves the CPU idle most of the time
        /// (a short tile, then a pause twice as long), so the audio never stutters on a slow laptop.
        /// </summary>
        private void YieldToReading()
        {
            while (!_closing && Now() < System.Threading.Interlocked.Read(ref _hotUntil)) System.Threading.Thread.Sleep(30);
            if (_closing) throw new System.Threading.ThreadInterruptedException();   // the overlay closed: stop the index
            if (!_closing && _speaking)
            {
                long spent = Now() - System.Threading.Interlocked.Read(ref _lastYield);
                System.Threading.Thread.Sleep((int)Math.Max(60, Math.Min(400, 2 * spent)));
            }
            System.Threading.Interlocked.Exchange(ref _lastYield, Now());
        }

        private bool SafeYield()
        {
            try { YieldToReading(); return !_closing; }
            catch (System.Threading.ThreadInterruptedException) { return false; }
        }

        private volatile bool _speaking;
        private long _lastYield;
        private System.Threading.CancellationTokenSource _hotCts = new System.Threading.CancellationTokenSource();

        /// <summary>A selection is starting: hold the background index back, and abort the engine call in flight.</summary>
        private void MarkHot(int ms)
        {
            System.Threading.Interlocked.Exchange(ref _hotUntil, Now() + ms);
            var old = System.Threading.Interlocked.Exchange(ref _hotCts, new System.Threading.CancellationTokenSource());
            try { old.Cancel(); } catch { }
        }

        private void IndexStep(OcrService.Result r, string name, OcrPass p)
        {
            if (IsDisposed || _closing) return;
            _indexDone++;
            if (r != null && _ocr != null && _ocr.Problem.Length == 0)
            {
                _ocr = r;
                if (_status == "No text found on this screen." && r.Page.WordCount > 0) { _status = ""; InvalidateBottom(); }
            }
            AppStorage.Log("Overlay index: +" + name + " pass_ms=" + (p?.Ms ?? -1) + " merge_ms=" + (p?.MergeMs ?? -1) + " words=" + (_ocr?.Page.WordCount ?? 0) + " done=" + _indexDone + "/" + _indexJobs);
            if (_indexDone >= _indexJobs && !IndexReady)
            {
                IndexReady = true;
                IndexMs = (int)(DateTime.UtcNow - _openedAt).TotalMilliseconds;
                AppStorage.Log("Overlay index: ready open_to_ready_ms=" + IndexMs + " passes=" + (_ocr?.Passes.Count ?? 0) + " words=" + (_ocr?.Page.WordCount ?? 0) +
                               " lines=" + (_ocr?.Page.Lines.Count ?? 0) + " blocks=" + (_ocr?.Page.Blocks.Count ?? 0));
            }
        }

        /// <summary>The tap radius: a word this close to the click counts (24 px at 100%).</summary>
        private double TapRadius => S(24);

        private void OnUi(Action a)
        {
            try { if (!IsDisposed && IsHandleCreated) BeginInvoke(a); } catch { }
        }

        private OcrService.Result _regionOnly;
        private Box _waitBox;
        private PageLayout CurrentPage => _ocr != null && _ocr.Problem.Length == 0 ? _ocr.Page : _regionOnly?.Page;
        private string IndexState() => IndexReady ? "index" : _indexDone > 0 ? "partial" : "fast";

        // ---------------- Reading ----------------

        /// <summary>The app-wide reader (created once, primed at open), subscribed while this overlay is open.</summary>
        private IReadAloud Voice()
        {
            var shared = Voices.Shared(_silentVoice);
            if (!ReferenceEquals(shared, _reader))
            {
                if (_reader != null) _reader.Changed -= OnReaderChanged;
                _reader = shared;
                _reader.Changed += OnReaderChanged;
            }
            return _reader;
        }

        private void BeginSelection(string mode, DateTime releasedAt)
        {
            CancelPreview();
            _reader?.Stop();
            HideHighlight();
            LastReadText = "";
            LastReadWords = new List<OcrWord>();
            _selGen++;
            _pendingBox = null; _pendingTap = null;
            _releaseAt = releasedAt; _waitFirstWord = true; FirstWordMs = -1; WordsReadyMs = -1;
            LastMode = mode;
            ReadCount++;
            MarkHot(2500);
        }

        /// <summary>Reads a box (drag or two corners): every line it touches, whole, in reading order.</summary>
        public void ReadBox(Box box) => StartBox(box, "box");

        private void StartBox(Box box, string mode)
        {
            BeginSelection(mode, DateTime.UtcNow);
            DropTapPrep();
            if (CurrentPage == null)
            {
                // Within the first moment after opening: wait for the first look, and look at the box itself meanwhile.
                _pendingBox = box;
                _waitBox = box; Invalidate(BoxRect(box.Inflate(S(6))));
                InvalidateBottom();
                QuickLook(box);
                return;
            }
            ReadBoxNow(box, IndexState());
        }

        private async void QuickLook(Box box)
        {
            int gen = _selGen;
            var tasks = OcrService.QuickPasses(Source, box.Inflate(S(14)));
            if (tasks.Count == 0) return;
            _preparing++;
            try { await System.Threading.Tasks.Task.WhenAll(tasks); } catch { }
            finally { _preparing--; }
            if (IsDisposed || gen != _selGen || !_pendingBox.HasValue || CurrentPage != null) return;
            var r = new OcrService.Result();
            r.Passes.AddRange(tasks.Where(t => t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null).Select(t => t.Result));
            OcrService.Rebuild(r);
            _regionOnly = r;
            RegionPassesUsed = r.Passes.Count;
            var b = _pendingBox.Value;
            _pendingBox = null;
            ReadBoxNow(b, "quick");
        }

        private void ReadBoxNow(Box box, string state)
        {
            if (!_waitBox.IsEmpty) { Invalidate(BoxRect(_waitBox.Inflate(S(6)))); _waitBox = new Box(); }
            var page = CurrentPage;
            LastIndexState = state;
            if (page == null) { Say(_ocr != null && _ocr.Problem.Length > 0 ? _ocr.Problem : "Still reading the screen. Try again in a moment."); return; }
            var t0 = DateTime.UtcNow;
            var parts = page.Snap(box);
            AppStorage.Log("Overlay snap: mode=" + LastMode + " index=" + state + " blocks=" + parts.Count + " lines=" + parts.Sum(p => p.Words.Select(w => w.Line).Distinct().Count()) +
                           " words=" + parts.Sum(p => p.Words.Count) + " snap_ms=" + (int)(DateTime.UtcNow - t0).TotalMilliseconds +
                           " kinds=" + string.Join(",", parts.Select(p => p.Kind.ToString()[0] + ":" + (int)(p.LineCoverage * 100) + "%")));
            if (parts.Count == 0) { Say(NoTextThere); return; }
            Show(parts);
        }

        // ---- taps

        private DateTime _tapReleasedAt;

        /// <summary>At the release of a tap: find its paragraph now and start preparing the speech (dropped if it turns into a box).</summary>
        private void PrepareTap(double x, double y)
        {
            _tapReleasedAt = DateTime.UtcNow;
            MarkHot(1500);
            _tapPrep = CurrentPage?.ParagraphAt(x, y, TapRadius);
            _tapPrepText = null;
            if (_tapPrep == null || _tapPrep.Words.Count == 0) { _tapPrep = null; return; }
            try
            {
                _tapPrepText = new SpokenText(PageLayout.Dedupe(_tapPrep.Words)).Text;
                Voice().Prepare(_tapPrepText);
                TapsPrepared++;
            }
            catch (Exception e) { AppStorage.Log("Overlay tap prepare failed: " + e.GetType().Name); }
        }

        private void DropTapPrep()
        {
            if (_tapPrep == null && _tapPrepText == null) return;
            _tapPrep = null; _tapPrepText = null;
            try { _reader?.Prepare(null); } catch { }
        }

        private void ReadTap(double x, double y)
        {
            var prep = _tapPrep;
            BeginSelection("tap", _tapReleasedAt == default(DateTime) ? DateTime.UtcNow : _tapReleasedAt);
            _tapPrep = prep;
            if (CurrentPage == null) { _pendingTap = (x, y); InvalidateBottom(); return; }
            ReadTapNow(x, y);
        }

        private void ReadTapNow(double x, double y)
        {
            var part = _tapPrep ?? CurrentPage?.ParagraphAt(x, y, TapRadius);
            _tapPrep = null; _tapPrepText = null;
            LastIndexState = IndexState();
            if (part != null)
            {
                AppStorage.Log("Overlay tap: paragraph lines=" + part.Words.Select(w => w.Line).Distinct().Count() + " words=" + part.Words.Count + " index=" + LastIndexState);
                Show(new List<SnapPart> { part });
                return;
            }
            TapPatch(x, y);
        }

        /// <summary>No word near a tap: look closer at the patch around it, then read the nearest paragraph there.</summary>
        private async void TapPatch(double x, double y)
        {
            int gen = _selGen;
            TapPatches++;
            var patch = new Box(x - S(300), y - S(100), S(600), S(200));
            var t0 = DateTime.UtcNow;
            var tasks = OcrService.QuickPasses(Source, patch);
            _preparing++;
            InvalidateBottom();
            try { await System.Threading.Tasks.Task.WhenAny(System.Threading.Tasks.Task.WhenAll(tasks), System.Threading.Tasks.Task.Delay(TapPatchBudgetMs)); }
            finally { _preparing--; }
            if (IsDisposed || gen != _selGen) return;
            var r = new OcrService.Result();
            r.Passes.AddRange(tasks.Where(t => t.Status == System.Threading.Tasks.TaskStatus.RanToCompletion && t.Result != null).Select(t => t.Result));
            OcrService.Rebuild(r);
            var part = r.Page.ParagraphAt(x, y, TapRadius);
            AppStorage.Log("Overlay tap patch: passes=" + r.Passes.Count + " found=" + (part != null) + " ms=" + (int)(DateTime.UtcNow - t0).TotalMilliseconds);
            LastIndexState = "patch";
            if (part == null) { Say(NoTextThere); return; }
            Show(new List<SnapPart> { part });
        }

        /// <summary>Outlines what was picked (numbered when several blocks) and starts reading at once.</summary>
        private void Show(List<SnapPart> parts)
        {
            HideHighlight();
            InvalidatePreview();
            _preview = parts;
            PreviewParts = parts.Count;
            _previewStrong = true;
            InvalidatePreview();
            _previewTimer.Stop();
            _previewTimer.Start();
            var words = PageLayout.Dedupe(PageLayout.WordsOf(parts));   // across blocks too: never a word twice
            WordsReadyMs = (int)(DateTime.UtcNow - _releaseAt).TotalMilliseconds;
            ReadWords(words);
        }

        private void CancelPreview()
        {
            _previewTimer.Stop();
            InvalidatePreview();
            _preview = null;
        }

        private void InvalidatePreview()
        {
            if (_preview == null) return;
            foreach (var p in _preview) Invalidate(BoxRect(p.Box.Inflate(S(8))));
        }

        private void ReadWords(List<OcrWord> words)
        {
            if (words.Count == 0) { Say(NoTextThere); return; }
            _spoken = new SpokenText(words);
            LastReadText = _spoken.Text;
            LastReadWords = words.ToList();
            _status = "";
            try
            {
                Voice().Read(_spoken.Text);
                AppStorage.Log("Overlay read: words=" + words.Count + " lines=" + words.Select(w => w.Line).Distinct().Count() + " voice=\"" + _reader.VoiceName + "\"");
            }
            catch (Exception e) { VoiceFailed(e); }
            InvalidateBottom();
        }

        private void Say(string message)
        {
            CancelPreview();
            _spoken = null;
            LastReadText = message;
            _status = message;
            HideHighlight();
            try { Voice().Read(message); } catch (Exception e) { VoiceFailed(e); }
            InvalidateBottom();
        }

        private void VoiceFailed(Exception e)
        {
            AppStorage.Log("Voice failed: " + e.GetType().Name + " " + e.Message);
            if (_reader != null) _reader.Changed -= OnReaderChanged;
            _reader = null;
            Voices.ResetShared();
            _status = SpeechReader.CountVoices() == 0 ? "No Windows voice is installed." : "No speaker found. Check your sound output.";
        }

        private void OnReaderChanged()
        {
            if (_reader == null) return;
            _speaking = _reader.Status == ReadState.Playing || _reader.Status == ReadState.Preparing;
            if (_waitFirstWord && _reader.Status == ReadState.Playing && _reader.AudioStartedAt != DateTime.MinValue && _reader.AudioStartedAt >= _releaseAt)
            {
                _waitFirstWord = false;
                FirstWordMs = (int)(_reader.AudioStartedAt - _releaseAt).TotalMilliseconds;
                System.Threading.Interlocked.Exchange(ref _hotUntil, Now() + 1200);   // the index waits a little longer so the start stays smooth
                AppStorage.Log("Overlay latency: mode=" + LastMode + " release_to_words_ms=" + WordsReadyMs + " release_to_audio_ms=" + FirstWordMs +
                               " index=" + LastIndexState + " passes=" + (_ocr?.Passes.Count ?? 0) + " voice=" + (_reader is OneCoreReader ? "onecore" : "sapi") +
                               " open_age_ms=" + (int)(_releaseAt - _openedAt).TotalMilliseconds);
            }
            if (_spoken != null && (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Paused))
            {
                // The voice's word (it may say a code token as several words) maps back to the OCR word by position.
                int cw = _reader.CurrentWord;
                var plan = _reader.Plan;
                int ocrWord = plan != null && cw >= 0 && cw < plan.Words.Count ? _spoken.WordAtChar(plan.Words[cw].Start) : Math.Max(0, cw);
                var target = _spoken.HighlightFor(ocrWord);
                if (!_hlVisible || !SameBox(target, _hlTarget))
                {
                    _hlFrom = _hlVisible ? _hl : target;
                    _hlTarget = target;
                    _hlMoved = DateTime.UtcNow;
                    _hlVisible = true;
                    HighlightMoves++;
                }
                _anim.Start();
            }
            else if (_reader.Status == ReadState.Finished || _reader.Status == ReadState.Idle)
            {
                HideHighlight();
                if (!Previewing && _reader.Status == ReadState.Finished) { InvalidatePreview(); _preview = null; }
                if (_reader.Status == ReadState.Finished && _status == LastReadText) _status = ""; // a spoken message is done
            }
            InvalidateBottom();
        }

        private static bool SameBox(Box a, Box b) => Math.Abs(a.X - b.X) < 0.5 && Math.Abs(a.Y - b.Y) < 0.5 && Math.Abs(a.W - b.W) < 0.5;

        private void HideHighlight()
        {
            if (!_hlVisible) return;
            _hlVisible = false;
            Invalidate(_lastHlRect);
        }

        public void Use(Control c)
        {
            switch (c)
            {
                case Control.Back: _reader?.Skip(-ReadingPlan.SkipWords); break;
                case Control.Forward: _reader?.Skip(ReadingPlan.SkipWords); break;
                case Control.PlayPause: _reader?.TogglePause(); break;
                case Control.Stop: CancelPreview(); _reader?.Stop(); HideHighlight(); break;
                case Control.Close: Close(); break;
            }
            ControlUsed?.Invoke(c);
            InvalidateBottom();
        }

        private bool Reading => _reader != null && (_reader.Status == ReadState.Playing || _reader.Status == ReadState.Paused);

        // ---------------- Input: the three gestures (see BoxSelector) ----------------

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            switch (e.KeyCode)
            {
                case Keys.Escape:
                    if (_selector.Busy) CancelGesture();   // Esc cancels corner 1 (or a pending tap / drag) first
                    else Close();
                    break;
                case Keys.Space: Use(Control.PlayPause); break;
                case Keys.Left: Use(Control.Back); break;
                case Keys.Right: Use(Control.Forward); break;
            }
        }

        private void CancelGesture()
        {
            bool wasCorner = _selector.WaitingForSecondCorner;
            _selector.Cancel();
            _tapTimer.Stop();
            DropTapPrep();
            if (wasCorner) AppStorage.Log("Overlay gesture: corners cancelled");
            InvalidateBox();
            InvalidateBottom();
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button == MouseButtons.Right) { if (_selector.Busy) CancelGesture(); return; }
            if (e.Button != MouseButtons.Left) return;
            if (!_selector.WaitingForSecondCorner)
            {
                _pressed = HitControl(e.Location);
                if (_pressed != Control.None) return;
            }
            Selection(_selector.OnDown(e.X, e.Y, Now()));
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            var c = HitControl(e.Location) != Control.None && !_selector.WaitingForSecondCorner ? Cursors.Hand : Cursors.Cross;
            if (Cursor != c) Cursor = c;
            if (_pressed != Control.None) return;
            var before = _selector.Current;
            var r = _selector.OnMove(e.X, e.Y, Now());
            if (r.Kind != BoxSelector.Kind.None) Selection(r);
            else if (_selector.HasLiveBox) InvalidateBox();   // the box follows in place (double-buffered, no blink)
            if (_selector.Current != before) InvalidateBottom();
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button != MouseButtons.Left) return;
            if (_pressed != Control.None)
            {
                var c = _pressed;
                _pressed = Control.None;
                if (HitControl(e.Location) == c) Use(c);
                return;
            }
            _boxMode = _selector.WaitingForSecondCorner ? "corners" : "box";
            Selection(_selector.OnUp(e.X, e.Y, Now()));
        }

        private string _boxMode = "box";

        private void Selection(BoxSelector.Result r)
        {
            switch (r.Kind)
            {
                case BoxSelector.Kind.TapPending:
                    PrepareTap(r.X, r.Y);
                    _tapTimer.Stop(); _tapTimer.Start();
                    break;
                case BoxSelector.Kind.CornerSet:
                    _tapTimer.Stop();
                    DropTapPrep();
                    AppStorage.Log("Overlay gesture: corner 1 set");
                    break;
                case BoxSelector.Kind.Tap:
                    _tapTimer.Stop();
                    ReadTap(r.X, r.Y);
                    break;
                case BoxSelector.Kind.Box:
                    _tapTimer.Stop();
                    AppStorage.Log("Overlay box: mode=" + _boxMode + " " + (int)r.Box.W + "x" + (int)r.Box.H);
                    InvalidateBox();
                    StartBox(r.Box, _boxMode);
                    break;
                case BoxSelector.Kind.Cancelled:
                    DropTapPrep();
                    break;
            }
            InvalidateBox();
            InvalidateBottom();
        }

        // Self-test helpers: the same state machine and paths real input takes (with synthetic times).
        public void SimulateDrag(Box b)
        {
            CancelGesture();
            long t = Now();
            Selection(_selector.OnDown(b.X, b.Y, t));
            _selector.OnMove(b.X + b.W / 2, b.Y + b.H / 2, t + 20);
            _selector.OnMove(b.Right, b.Bottom, t + 40);
            _boxMode = "box";
            Selection(_selector.OnUp(b.Right, b.Bottom, t + 60));
        }

        /// <summary>Self-test: forget a pending corner or tap.</summary>
        public void CancelCorner() => CancelGesture();

        /// <summary>Self-test: the next read uses the voice now saved in Settings (the shared reader is re-created).</summary>
        public void ResetVoice()
        {
            if (_reader != null) { _reader.Changed -= OnReaderChanged; try { _reader.Stop(); } catch { } }
            _reader = null;
            Voices.ResetShared();
        }

        public void SimulateTwoClicks(Box b)
        {
            CancelGesture();
            long t = Now();
            Selection(_selector.OnDown(b.X, b.Y, t));
            Selection(_selector.OnUp(b.X, b.Y, t + 30));
            Selection(_selector.OnMove(b.Right, b.Bottom, t + 120));   // moved within the tap window: corner 1 set
            Selection(_selector.OnDown(b.Right, b.Bottom, t + 600));
            _boxMode = "corners";
            Selection(_selector.OnUp(b.Right, b.Bottom, t + 650));
        }

        /// <summary>Self-test: a tap (the tap window is skipped; latency is from this call).</summary>
        public void SimulateTap(double x, double y)
        {
            CancelGesture();
            long t = Now();
            Selection(_selector.OnDown(x, y, t));
            Selection(_selector.OnUp(x, y, t + 40));
            Selection(_selector.Tick(t + 40 + BoxSelector.TapWindowMs));
        }

        // ---------------- Painting ----------------

        private void Animate()
        {
            bool moving = _hlVisible && (DateTime.UtcNow - _hlMoved).TotalMilliseconds < 110;
            if (_hlVisible)
            {
                double t = Math.Min(1, (DateTime.UtcNow - _hlMoved).TotalMilliseconds / 90.0);
                t = 1 - Math.Pow(1 - t, 3);
                _hl = new Box(Lerp(_hlFrom.X, _hlTarget.X, t), Lerp(_hlFrom.Y, _hlTarget.Y, t), Lerp(_hlFrom.W, _hlTarget.W, t), Lerp(_hlFrom.H, _hlTarget.H, t));
                var r = HighlightRect();
                Invalidate(Rectangle.Union(r, _lastHlRect));
            }
            if (!moving && !_hlVisible) _anim.Stop();
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private Rectangle HighlightRect()
        {
            var b = _hl.Inflate(S(4));
            var r = Rectangle.FromLTRB((int)Math.Floor(b.X), (int)Math.Floor(b.Y), (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
            r.Inflate(Si(8), Si(8));
            return r;
        }

        private Rectangle BoxRect(Box b)
        {
            var r = Rectangle.FromLTRB((int)b.X, (int)b.Y, (int)Math.Ceiling(b.Right), (int)Math.Ceiling(b.Bottom));
            r.Inflate(Si(16), Si(16));
            return r;
        }

        private void InvalidateBox()
        {
            var now = _selector.HasLiveBox ? BoxRect(_selector.LiveBox) : Rectangle.Empty;
            if (!_lastBoxRect.IsEmpty) Invalidate(_lastBoxRect);
            if (!now.IsEmpty) Invalidate(now);
            _lastBoxRect = now;
        }

        private void InvalidateBottom()
        {
            Invalidate(new Rectangle(0, ClientSize.Height - Si(150), ClientSize.Width, Si(150)));
            Invalidate(new Rectangle(ClientSize.Width - Si(90), 0, Si(90), Si(90)));
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            var clip = e.ClipRectangle;
            g.CompositingMode = CompositingMode.SourceCopy;
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.DrawImage(_dark, clip, clip, GraphicsUnit.Pixel);
            g.CompositingMode = CompositingMode.SourceOver;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

            if (_selector.HasLiveBox)
            {
                var b = _selector.LiveBox;
                var r = new RectangleF((float)b.X, (float)b.Y, (float)Math.Max(1, b.W), (float)Math.Max(1, b.H));
                using (var path = Rounded(r, S(14)))
                {
                    using (var fill = new SolidBrush(Color.FromArgb(22, Green))) g.FillPath(fill, path);
                    using (var glow = new Pen(Color.FromArgb(70, Green), S(8))) g.DrawPath(glow, path);
                    using (var pen = new Pen(Green, S(3))) g.DrawPath(pen, path);
                }
                _lastBoxRect = BoxRect(b);
            }

            if (!_waitBox.IsEmpty)
            {
                // The released box stays (softly) while its sharper passes finish (well under a second).
                var r = new RectangleF((float)_waitBox.X, (float)_waitBox.Y, (float)Math.Max(1, _waitBox.W), (float)Math.Max(1, _waitBox.H));
                using (var path = Rounded(r, S(14)))
                using (var pen = new Pen(Color.FromArgb(150, Green), S(2))) g.DrawPath(pen, path);
            }

            if (_preview != null)
            {
                int n = 0;
                foreach (var p in _preview)
                {
                    n++;
                    var b = p.Box.Inflate(S(5));
                    var r = new RectangleF((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                    using (var path = Rounded(r, S(12)))
                    {
                        using (var fill = new SolidBrush(Color.FromArgb(_previewStrong ? 34 : 14, Green))) g.FillPath(fill, path);
                        using (var pen = new Pen(Color.FromArgb(_previewStrong ? 200 : 90, Green), S(_previewStrong ? 2 : 1.4))) g.DrawPath(pen, path);
                    }
                    if (_preview.Count > 1 || _previewStrong)
                    {
                        float d = S(20);
                        var badge = new RectangleF(r.X - d * 0.4f, r.Y - d * 0.4f, d, d);
                        using (var bb = new SolidBrush(Color.FromArgb(_previewStrong ? 235 : 150, Green))) g.FillEllipse(bb, badge);
                        using (var ink = new SolidBrush(Color.FromArgb(15, 40, 20)))
                        using (var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                            g.DrawString(n.ToString(), _small, ink, badge, sf);
                    }
                }
            }

            if (_hlVisible)
            {
                double pulse = 0.5 + 0.5 * Math.Sin((DateTime.UtcNow - _born).TotalSeconds * Math.PI * 2 / 1.2);
                var b = _hl.Inflate(S(4));
                var r = new RectangleF((float)b.X, (float)b.Y, (float)b.W, (float)b.H);
                using (var path = Rounded(r, S(10)))
                {
                    using (var glow = new Pen(Color.FromArgb((int)(60 + 50 * pulse), Green), S(7))) g.DrawPath(glow, path);
                    using (var pen = new Pen(Color.FromArgb(255, 120, 255, 150), S(2.6))) g.DrawPath(pen, path);
                }
                _lastHlRect = HighlightRect();
            }

            DrawBottom(g);
            DrawClose(g);
        }

        private void DrawBottom(Graphics g)
        {
            _controls.Remove(Control.Back); _controls.Remove(Control.PlayPause); _controls.Remove(Control.Forward); _controls.Remove(Control.Stop);
            string text = ModeText();
            bool controls = Reading || (_reader != null && _reader.Status == ReadState.Paused);
            float pad = S(18), h = S(48), btn = S(36), gap = S(10);
            var size = g.MeasureString(text, _font);
            float w = Math.Min(ClientSize.Width - S(40), size.Width + pad * 2 + (controls ? 4 * btn + 4 * gap : 0));
            var pill = new RectangleF((ClientSize.Width - w) / 2f, ClientSize.Height - S(40) - h, w, h);
            using (var path = Rounded(pill, h))
            {
                using (var bg = new SolidBrush(Color.FromArgb(235, 22, 24, 30))) g.FillPath(bg, path);
                using (var rim = new Pen(Color.FromArgb(90, Green), S(1.2f))) g.DrawPath(rim, path);
            }
            float x = pill.X + pad;
            if (controls)
            {
                foreach (var c in new[] { Control.Back, Control.PlayPause, Control.Forward, Control.Stop })
                {
                    var r = new RectangleF(x, pill.Y + (h - btn) / 2, btn, btn);
                    _controls[c] = Rectangle.Round(r);
                    using (var b = new SolidBrush(c == Control.PlayPause ? Green : Color.FromArgb(55, 255, 255, 255))) g.FillEllipse(b, r);
                    DrawGlyph(g, c, r, c == Control.PlayPause ? Color.FromArgb(20, 30, 20) : Color.White, _reader.Status == ReadState.Playing);
                    x += btn + gap;
                }
                x += gap;
            }
            using (var b = new SolidBrush(Color.FromArgb(240, 240, 244)))
            using (var sf = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter, LineAlignment = StringAlignment.Center })
                g.DrawString(controls ? "Reading…" : text, _font, b, new RectangleF(x, pill.Y, pill.Right - x - pad, h), sf);
        }

        /// <summary>The mode indicator on the floating pill.</summary>
        public string ModeText()
        {
            if (_status.Length > 0) return _status;
            if (_selector.WaitingForSecondCorner) return "Corner 1 set: move and click again";
            if (_selector.Current == BoxSelector.State.Dragging) return "Release to read";
            if (Previewing && _selector.Current != BoxSelector.State.TapPending) return "Reading…";
            return "Tap to read · drag a box · or click, move, click. Esc closes.";
        }

        private void DrawClose(Graphics g)
        {
            float d = S(40);
            var r = new RectangleF(ClientSize.Width - d - S(28), S(28), d, d);
            _controls[Control.Close] = Rectangle.Round(r);
            using (var b = new SolidBrush(Color.FromArgb(220, 22, 24, 30))) g.FillEllipse(b, r);
            using (var rim = new Pen(Color.FromArgb(120, 255, 255, 255), S(1))) g.DrawEllipse(rim, r);
            float c = S(8);
            using (var p = new Pen(Color.White, S(2.4f)) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            {
                float cx = r.X + r.Width / 2, cy = r.Y + r.Height / 2;
                g.DrawLine(p, cx - c, cy - c, cx + c, cy + c);
                g.DrawLine(p, cx - c, cy + c, cx + c, cy - c);
            }
        }

        private void DrawGlyph(Graphics g, Control b, RectangleF r, Color ink, bool playing)
        {
            float cx = r.X + r.Width / 2f, cy = r.Y + r.Height / 2f, u = r.Width / 32f;
            using (var brush = new SolidBrush(ink))
            using (var pen = new Pen(ink, 2f * u) { StartCap = LineCap.Round, EndCap = LineCap.Round })
            using (var tiny = new Font("Segoe UI", 8f * u, FontStyle.Bold, GraphicsUnit.Pixel))
            {
                switch (b)
                {
                    case Control.PlayPause:
                        if (playing)
                        {
                            g.FillRectangle(brush, cx - 6 * u, cy - 7 * u, 4 * u, 14 * u);
                            g.FillRectangle(brush, cx + 2 * u, cy - 7 * u, 4 * u, 14 * u);
                        }
                        else g.FillPolygon(brush, new[] { new PointF(cx - 4 * u, cy - 8 * u), new PointF(cx + 8 * u, cy), new PointF(cx - 4 * u, cy + 8 * u) });
                        break;
                    case Control.Stop:
                        g.FillRectangle(brush, cx - 5.5f * u, cy - 5.5f * u, 11 * u, 11 * u);
                        break;
                    default:
                        bool back = b == Control.Back;
                        g.DrawArc(pen, new RectangleF(cx - 9 * u, cy - 9 * u, 18 * u, 18 * u), back ? 200 : -20, back ? 250 : -250);
                        float ax = back ? cx - 8.5f * u : cx + 8.5f * u, ay = cy - 5 * u;
                        g.FillPolygon(brush, new[] { new PointF(ax - 3 * u, ay - 1 * u), new PointF(ax + 3 * u, ay - 1 * u), new PointF(ax, ay + 4 * u) });
                        var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                        g.DrawString("10", tiny, brush, new RectangleF(cx - 8 * u, cy - 6 * u, 16 * u, 14 * u), sf);
                        break;
                }
            }
        }

        private Control HitControl(Point p)
        {
            foreach (var kv in _controls)
            {
                var r = kv.Value; r.Inflate(Si(4), Si(4));
                if (r.Contains(p)) return kv.Key;
            }
            return Control.None;
        }

        private static GraphicsPath Rounded(RectangleF r, float diameter)
        {
            var p = new GraphicsPath();
            diameter = Math.Max(2, Math.Min(diameter, Math.Min(r.Width, r.Height)));
            p.AddArc(r.X, r.Y, diameter, diameter, 180, 90);
            p.AddArc(r.Right - diameter, r.Y, diameter, diameter, 270, 90);
            p.AddArc(r.Right - diameter, r.Bottom - diameter, diameter, diameter, 0, 90);
            p.AddArc(r.X, r.Bottom - diameter, diameter, diameter, 90, 90);
            p.CloseFigure();
            return p;
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            _closing = true;
            try { _hotCts.Cancel(); } catch { }
            _anim.Stop();
            _previewTimer.Stop();
            _tapTimer.Stop();
            // The reader is shared for the app's lifetime: stop it and let go, never dispose it here.
            if (_reader != null) { _reader.Changed -= OnReaderChanged; try { _reader.Stop(); } catch { } _reader = null; }
            base.OnFormClosed(e);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _closing = true;
                _anim.Dispose();
                _previewTimer.Dispose();
                _tapTimer.Dispose();
                _dark.Dispose();
                _font.Dispose();
                _small.Dispose();
                _ocr = null;
                _spoken = null;
            }
            base.Dispose(disposing);
        }
    }
}
