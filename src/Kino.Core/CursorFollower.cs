namespace Kino;

/// <summary>
/// Moves a fixed-size recording region to follow the mouse pointer, like a camera. The pointer can roam
/// the middle of the region without moving it; once it pushes past that dead zone the region glides
/// after it with a critically damped spring, so it starts and stops smoothly and never overshoots. The
/// region stays inside <c>bounds</c>.
/// </summary>
/// <remarks>Not thread-safe; call <see cref="Next"/> once per frame from the capture thread.</remarks>
public sealed class CursorFollower
{
    /// <summary>The share of the region's half-size, either side of centre, the pointer can move
    /// within without the region following.</summary>
    public const double DeadZone = 0.5;

    /// <summary>Roughly how long the region takes to catch up with the pointer.</summary>
    public static readonly TimeSpan SmoothTime = TimeSpan.FromSeconds(0.3);

    private readonly int _width;
    private readonly int _height;
    private readonly PixelRect _bounds;
    private Axis _x;
    private Axis _y;

    /// <param name="start">The region as selected. Following keeps its size.</param>
    /// <param name="bounds">The area the region must stay inside, normally the virtual screen.</param>
    /// <param name="cursor">Where the pointer is when recording starts.</param>
    public CursorFollower(PixelRect start, PixelRect bounds, PixelPoint cursor)
    {
        _width = start.Width;
        _height = start.Height;
        _bounds = bounds;
        _x = new Axis(start.X + (start.Width / 2.0), start.Width / 2.0, cursor.X);
        _y = new Axis(start.Y + (start.Height / 2.0), start.Height / 2.0, cursor.Y);
        Region = start;
    }

    /// <summary>The region for the latest frame.</summary>
    public PixelRect Region { get; private set; }

    /// <summary>Advances by <paramref name="elapsed"/> towards <paramref name="cursor"/> and returns the
    /// region for the next frame.</summary>
    public PixelRect Next(PixelPoint cursor, TimeSpan elapsed)
    {
        var seconds = Math.Clamp(elapsed.TotalSeconds, 0, 0.25);
        var x = _x.Step(cursor.X, seconds, _bounds.X + (_width / 2.0), _bounds.Right - (_width / 2.0));
        var y = _y.Step(cursor.Y, seconds, _bounds.Y + (_height / 2.0), _bounds.Bottom - (_height / 2.0));
        Region = new PixelRect(
            Math.Clamp((int)Math.Round(x - (_width / 2.0)), _bounds.X, Math.Max(_bounds.X, _bounds.Right - _width)),
            Math.Clamp((int)Math.Round(y - (_height / 2.0)), _bounds.Y, Math.Max(_bounds.Y, _bounds.Bottom - _height)),
            _width,
            _height);
        return Region;
    }

    /// <summary>One axis of the camera: where it wants to be, where it is and how fast it is moving.</summary>
    private struct Axis
    {
        private readonly double _deadZone;
        private double _target;
        private double _position;
        private double _velocity;
        private double _allowance;

        public Axis(double centre, double halfSize, double pointer)
        {
            _deadZone = halfSize * DeadZone;
            _target = centre;
            _position = centre;
            _velocity = 0;
            // Recording starts with the pointer on the region's far corner, outside the dead zone. The
            // region holds still until the pointer moves further out; moving inward tightens the
            // allowance back to the dead zone.
            _allowance = Math.Max(_deadZone, Math.Abs(pointer - centre));
        }

        public double Step(double pointer, double seconds, double min, double max)
        {
            var offset = pointer - _target;
            _allowance = Math.Max(_deadZone, Math.Min(_allowance, Math.Abs(offset)));
            _target = pointer - Math.Clamp(offset, -_allowance, _allowance);
            _target = min <= max ? Math.Clamp(_target, min, max) : (min + max) / 2;
            _position = SmoothDamp(_position, _target, ref _velocity, SmoothTime.TotalSeconds, seconds);
            return _position;
        }

        // A critically damped spring (Game Programming Gems 4, 1.10), stable for any step length.
        private static double SmoothDamp(double current, double target, ref double velocity, double smoothTime, double seconds)
        {
            if (seconds <= 0)
            {
                return current;
            }

            var omega = 2 / smoothTime;
            var x = omega * seconds;
            var decay = 1 / (1 + x + (0.48 * x * x) + (0.235 * x * x * x));
            var change = current - target;
            var temp = (velocity + (omega * change)) * seconds;
            velocity = (velocity - (omega * temp)) * decay;
            var output = target + ((change + temp) * decay);
            if ((target - current > 0) == (output > target))
            {
                velocity = 0;
                return target;
            }

            return output;
        }
    }
}
