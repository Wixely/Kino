namespace Kino.Tests;

public sealed class CursorFollowerTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);
    private static readonly TimeSpan Frame = TimeSpan.FromSeconds(1 / 30.0);

    [Fact]
    public void Region_holds_still_while_the_pointer_stays_near_where_recording_started()
    {
        var start = new PixelRect(100, 100, 400, 300);
        // Recording starts with the pointer on the far corner.
        var follower = new CursorFollower(start, Screen, new PixelPoint(499, 399));

        Assert.Equal(start, follower.Next(new PixelPoint(499, 399), Frame));
        Assert.Equal(start, follower.Next(new PixelPoint(300, 250), Frame));
        Assert.Equal(start, follower.Next(new PixelPoint(390, 320), Frame));
    }

    [Fact]
    public void Region_glides_after_the_pointer_without_jumping_or_overshooting()
    {
        var start = new PixelRect(100, 100, 400, 300);
        var follower = new CursorFollower(start, Screen, new PixelPoint(300, 250));
        var pointer = new PixelPoint(1200, 250);

        var previous = start.X;
        var steps = new List<int>();
        for (var frame = 0; frame < 90; frame++)
        {
            var region = follower.Next(pointer, Frame);
            Assert.Equal(start.Width, region.Width);
            Assert.Equal(start.Y, region.Y);
            Assert.True(region.X >= previous, "The region moved backwards.");
            steps.Add(region.X - previous);
            previous = region.X;
        }

        // Smooth: the first step is small, no single step is a jump, and it settles with the pointer at
        // the edge of the dead zone (half of the half-width to the right of centre).
        Assert.InRange(steps[0], 1, 150);
        Assert.All(steps, step => Assert.InRange(step, 0, 150));
        Assert.Equal(1200 - 200 - 100, previous);
        Assert.Equal(0, steps[^1]);
    }

    [Fact]
    public void Region_stays_inside_the_screen()
    {
        var start = new PixelRect(1400, 700, 400, 300);
        var follower = new CursorFollower(start, Screen, new PixelPoint(1600, 850));

        PixelRect region = start;
        for (var frame = 0; frame < 120; frame++)
        {
            region = follower.Next(new PixelPoint(1919, 1079), Frame);
            Assert.True(region.Right <= Screen.Right && region.Bottom <= Screen.Bottom, $"{region} left the screen.");
        }

        Assert.Equal(new PixelRect(1520, 780, 400, 300), region);

        for (var frame = 0; frame < 120; frame++)
        {
            region = follower.Next(new PixelPoint(-50, -50), Frame);
            Assert.True(region.X >= 0 && region.Y >= 0, $"{region} left the screen.");
        }

        Assert.Equal(new PixelRect(0, 0, 400, 300), region);
    }

    [Fact]
    public void Long_pauses_between_frames_do_not_make_the_region_leap_past_the_pointer()
    {
        var follower = new CursorFollower(new PixelRect(0, 0, 400, 300), Screen, new PixelPoint(200, 150));

        var region = follower.Next(new PixelPoint(1000, 150), TimeSpan.FromSeconds(5));

        Assert.InRange(region.X, 1, 1000 - 200 - 100);
    }
}
