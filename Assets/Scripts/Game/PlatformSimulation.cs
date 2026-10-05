using System;

public struct PlatformState
{
    public double X;
    public double Y;
    public double VelocityX;
    public double VelocityY;
    public uint FootholdId;
    public bool Grounded;
    public bool JumpHeld;
}

public sealed class PlatformSimulation
{
    public const double FixedDeltaSeconds = 0.02;
    private const double Epsilon = 0.000001;
    private readonly MapGeometryData _geometry;

    private struct Collision
    {
        public double Time;
        public bool Hit;
        public bool BlockX;
        public bool BlockY;
        public bool Respawn;
        public uint FootholdId;
    }

    public PlatformSimulation(MapGeometryData geometry)
    {
        _geometry = geometry;
    }

    public void Reset(ref PlatformState state)
    {
        state = new PlatformState { X = _geometry.SpawnX, Y = _geometry.SpawnY, FootholdId = _geometry.SpawnFootholdId, Grounded = true };
    }

    public void Step(ref PlatformState state, int horizontal, bool jumpHeld)
    {
        bool jumpPressed = jumpHeld && !state.JumpHeld;
        state.JumpHeld = jumpHeld;
        state.VelocityX = horizontal * (double)_geometry.HorizontalSpeed;
        if (state.Grounded && jumpPressed)
        {
            state.Grounded = false;
            state.FootholdId = 0;
            state.VelocityY = _geometry.JumpSpeed;
        }

        double seconds = FixedDeltaSeconds;
        if (state.Grounded)
        {
            double dx = state.VelocityX * seconds;
            double remaining = MoveGround(ref state, dx);
            if (state.Grounded)
                return;

            seconds *= Math.Abs(remaining / dx);
        }

        MoveAir(ref state, seconds);
    }

    private FootholdData FindSupport(double x, uint id)
    {
        for (int i = 0; i < _geometry.Footholds.Count; ++i)
        {
            FootholdData foothold = _geometry.Footholds[id];
            if (x < foothold.X1 - Epsilon)
                id = foothold.PrevId;
            else if (x > foothold.X2 + Epsilon)
                id = foothold.NextId;
            else
                return foothold;
        }

        throw new InvalidOperationException("Missing support foothold");
    }

    private double MoveGround(ref PlatformState state, double dx)
    {
        FootholdData current = _geometry.Footholds[state.FootholdId];
        FootholdData last = current;
        double target = state.X + dx;
        for (int i = 0; i < _geometry.Footholds.Count; ++i)
        {
            uint neighborId = target > last.X2 + Epsilon ? last.NextId : target < last.X1 - Epsilon ? last.PrevId : 0;
            if (!_geometry.Footholds.TryGetValue(neighborId, out FootholdData neighbor))
                break;

            last = neighbor;
        }

        double groundDx = Math.Clamp(target, last.X1, last.X2) - state.X;
        Collision collision = FindCollision(state.X, state.Y, groundDx, 0);
        if (collision.Hit)
        {
            state.X += groundDx * collision.Time;
            state.VelocityX = 0;
            state.FootholdId = FindSupport(state.X, current.Id).Id;
            return 0;
        }

        state.X += groundDx;
        state.FootholdId = FindSupport(state.X, current.Id).Id;
        double remaining = dx - groundDx;
        if (Math.Abs(remaining) > Epsilon)
        {
            state.Grounded = false;
            state.FootholdId = 0;
        }

        return remaining;
    }

    private void MoveAir(ref PlatformState state, double seconds)
    {
        state.VelocityY = Math.Max(-(double)_geometry.MaxFallSpeed, state.VelocityY - _geometry.Gravity * seconds);
        double dx = state.VelocityX * seconds;
        double dy = state.VelocityY * seconds;
        for (int i = 0; i < 8 && (Math.Abs(dx) > Epsilon || Math.Abs(dy) > Epsilon); ++i)
        {
            Collision collision = FindCollision(state.X, state.Y, dx, dy);
            state.X += dx * collision.Time;
            state.Y += dy * collision.Time;
            if (!collision.Hit)
                break;

            if (collision.Respawn)
            {
                bool jumpHeld = state.JumpHeld;
                Reset(ref state);
                state.JumpHeld = jumpHeld;
                return;
            }

            dx *= 1 - collision.Time;
            dy *= 1 - collision.Time;
            if (collision.BlockX)
            {
                state.VelocityX = 0;
                dx = 0;
            }

            if (collision.BlockY)
            {
                state.VelocityY = 0;
                dy = 0;
            }

            if (collision.FootholdId != 0)
            {
                state.Grounded = true;
                state.FootholdId = collision.FootholdId;
                state.Y = _geometry.Footholds[collision.FootholdId].Y1 + (double)_geometry.HalfHeight;
                dx = MoveGround(ref state, dx);
            }
        }
    }

    private Collision FindCollision(double x, double y, double dx, double dy)
    {
        Collision nearest = new() { Time = 1 };
        void Consider(double time, bool blockX, bool blockY, uint footholdId, bool respawn)
        {
            if (time < -Epsilon || time > 1 + Epsilon)
                return;

            time = Math.Clamp(time, 0, 1);
            if (!nearest.Hit || time < nearest.Time - Epsilon)
                nearest = new Collision { Time = time, Hit = true, BlockX = blockX, BlockY = blockY, Respawn = respawn, FootholdId = footholdId };
            else if (Math.Abs(time - nearest.Time) <= Epsilon)
            {
                nearest.BlockX |= blockX;
                nearest.BlockY |= blockY;
                nearest.Respawn |= respawn;
                if (footholdId != 0 && (nearest.FootholdId == 0 || footholdId < nearest.FootholdId))
                    nearest.FootholdId = footholdId;
            }

            if (nearest.FootholdId != 0)
                nearest.Respawn = false;
        }

        foreach (ColliderData collider in _geometry.Colliders.Values)
        {
            double entryX = double.NegativeInfinity;
            double entryY = double.NegativeInfinity;
            double exitX = double.PositiveInfinity;
            double exitY = double.PositiveInfinity;
            if (!SweepAxis(x, dx, collider.MinX - (double)_geometry.HalfWidth, collider.MaxX + (double)_geometry.HalfWidth, ref entryX, ref exitX) || !SweepAxis(y, dy, collider.MinY - (double)_geometry.HalfHeight, collider.MaxY + (double)_geometry.HalfHeight, ref entryY, ref exitY))
                continue;

            double entry = Math.Max(entryX, entryY);
            double exit = Math.Min(exitX, exitY);
            if (entry >= -Epsilon && exit > Math.Max(0, entry) + Epsilon)
                Consider(entry, entryX >= entryY - Epsilon, entryY >= entryX - Epsilon, 0, false);
        }

        // 서버와 같은 교차 시점으로 착지와 벽 충돌을 예측한다.
        if (dy < -Epsilon)
        {
            foreach (FootholdData foothold in _geometry.Footholds.Values)
            {
                double time = (foothold.Y1 + (double)_geometry.HalfHeight - y) / dy;
                double crossingX = x + dx * time;
                if (crossingX < foothold.X1 - Epsilon || crossingX > foothold.X2 + Epsilon)
                    continue;

                if (time <= Epsilon && ((crossingX <= foothold.X1 + Epsilon && dx < 0) || (crossingX >= foothold.X2 - Epsilon && dx > 0)))
                    continue;

                Consider(time, false, true, foothold.Id, false);
            }
        }

        MapInfoData bounds = _geometry.Bounds;
        if (dx < -Epsilon)
            Consider((bounds.MinX + (double)_geometry.HalfWidth - x) / dx, true, false, 0, false);
        else if (dx > Epsilon)
            Consider((bounds.MaxX - (double)_geometry.HalfWidth - x) / dx, true, false, 0, false);

        if (dy > Epsilon)
            Consider((bounds.MaxY - (double)_geometry.HalfHeight - y) / dy, false, true, 0, false);
        else if (dy < -Epsilon)
            Consider((bounds.MinY + (double)_geometry.HalfHeight - y) / dy, false, true, 0, true);

        return nearest;
    }

    private static bool SweepAxis(double position, double delta, double minimum, double maximum, ref double entry, ref double exit)
    {
        if (Math.Abs(delta) <= Epsilon)
            return position > minimum + Epsilon && position < maximum - Epsilon;

        entry = (minimum - position) / delta;
        exit = (maximum - position) / delta;
        if (entry > exit)
            (entry, exit) = (exit, entry);

        return true;
    }
}
