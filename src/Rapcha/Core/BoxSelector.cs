using System;

namespace Rapcha.Core
{
    /// <summary>
    /// How the overlay turns pointer input into a selection. Works for a mouse and a touchpad:
    /// - press, drag, release: a box (touchpad tap-and-drag and click-hold-drag are the same events);
    /// - click on empty space, move, click again: a box from the two corners (no holding needed);
    /// - click on a word without moving: read that word's line;
    /// - movement under 6 px counts as a tap.
    /// </summary>
    public sealed class BoxSelector
    {
        public const int TapSlopPx = 6;

        public enum Kind { None, Box, TapWord, CornerSet, Cancelled }

        public struct Result
        {
            public Kind Kind;
            public Box Box;
            public double X, Y;
        }

        private enum State { Idle, Pressing, Dragging, CornerSet, SecondPress }

        private State _state = State.Idle;
        private double _ax, _ay, _cx, _cy, _px, _py;

        public bool HasLiveBox => _state == State.Dragging || _state == State.CornerSet || _state == State.SecondPress;
        public Box LiveBox => HasLiveBox ? Box.FromCorners(_ax, _ay, _cx, _cy) : new Box();
        public bool WaitingForSecondCorner => _state == State.CornerSet;

        public void OnDown(double x, double y)
        {
            _px = x; _py = y;
            if (_state == State.CornerSet) { _state = State.SecondPress; _cx = x; _cy = y; return; }
            _state = State.Pressing;
            _ax = x; _ay = y; _cx = x; _cy = y;
        }

        public void OnMove(double x, double y)
        {
            switch (_state)
            {
                case State.Pressing:
                    if (Far(x, y, _ax, _ay)) _state = State.Dragging;
                    _cx = x; _cy = y;
                    break;
                case State.Dragging:
                case State.CornerSet:
                case State.SecondPress:
                    _cx = x; _cy = y;
                    break;
            }
        }

        /// <param name="wordAtPoint">Is there an OCR word under the release point? (null = not known yet)</param>
        public Result OnUp(double x, double y, bool? wordAtPoint)
        {
            var r = new Result { Kind = Kind.None, X = x, Y = y };
            switch (_state)
            {
                case State.Pressing:
                    if (wordAtPoint == true) { _state = State.Idle; r.Kind = Kind.TapWord; }
                    else { _state = State.CornerSet; _ax = x; _ay = y; _cx = x; _cy = y; r.Kind = Kind.CornerSet; }
                    return r;
                case State.Dragging:
                    _state = State.Idle;
                    r.Box = Box.FromCorners(_ax, _ay, x, y);
                    r.Kind = Kind.Box;
                    return r;
                case State.SecondPress:
                    _state = State.Idle;
                    if (!Far(x, y, _ax, _ay)) { r.Kind = Kind.Cancelled; return r; } // clicked the same spot again
                    r.Box = Box.FromCorners(_ax, _ay, x, y);
                    r.Kind = Kind.Box;
                    return r;
                default:
                    _state = State.Idle;
                    return r;
            }
        }

        /// <summary>A tap whose word was unknown (OCR still running) turned out to be on empty space.</summary>
        public void SetCorner(double x, double y)
        {
            _state = State.CornerSet;
            _ax = x; _ay = y; _cx = x; _cy = y;
        }

        public void Cancel() => _state = State.Idle;

        private static bool Far(double x, double y, double x0, double y0) => Math.Max(Math.Abs(x - x0), Math.Abs(y - y0)) >= TapSlopPx;
    }
}
