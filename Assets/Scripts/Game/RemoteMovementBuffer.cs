using System;
using System.Collections.Generic;

public sealed class RemoteMovementBuffer
{
    public const int MaxSnapshots = 64;
    public const double InterpolationSeconds = 0.1;
    private readonly List<MovementSnapshot> _snapshots = new();
    private double _receivedAt;
    private double _renderTick;
    public int Count => _snapshots.Count;

    public bool Add(MovementSnapshot snapshot, double now)
    {
        if (_snapshots.Count > 0)
        {
            MovementSnapshot last = _snapshots[_snapshots.Count - 1];
            if (snapshot.ServerTick < last.ServerTick || (snapshot.ServerTick == last.ServerTick && snapshot.Reason == MovementStateReason.Normal))
                return false;
            if (snapshot.Reason == MovementStateReason.Respawned || Math.Abs(snapshot.X - last.X) > 80 || Math.Abs(snapshot.Y - last.Y) > 80)
                Reset();
            else if (snapshot.ServerTick == last.ServerTick)
                _snapshots.RemoveAt(_snapshots.Count - 1);
        }
        if (_snapshots.Count == 0)
            _renderTick = snapshot.ServerTick - InterpolationSeconds / PlatformSimulation.FixedDeltaSeconds;
        _snapshots.Add(snapshot);
        if (_snapshots.Count > MaxSnapshots)
            _snapshots.RemoveAt(0);
        _receivedAt = now;
        return true;
    }

    public bool Sample(double now, out double x, out double y)
    {
        x = y = 0;
        if (_snapshots.Count == 0)
            return false;
        MovementSnapshot last = _snapshots[_snapshots.Count - 1];
        double desired = last.ServerTick + Math.Max(0, now - _receivedAt) / PlatformSimulation.FixedDeltaSeconds -
            InterpolationSeconds / PlatformSimulation.FixedDeltaSeconds;
        // 지연 편차가 생겨도 표시 시각을 뒤로 돌리지 않고 마지막 수신 상태까지만 보간한다.
        _renderTick = Math.Max(_renderTick, Math.Min(last.ServerTick, desired));
        while (_snapshots.Count > 2 && _snapshots[1].ServerTick <= _renderTick)
            _snapshots.RemoveAt(0);
        MovementSnapshot first = _snapshots[0];
        if (_renderTick <= first.ServerTick || _snapshots.Count == 1)
        {
            x = first.X; y = first.Y;
            return true;
        }
        for (int i = 1; i < _snapshots.Count; ++i)
        {
            MovementSnapshot next = _snapshots[i];
            if (_renderTick <= next.ServerTick)
            {
                double blend = Math.Clamp((_renderTick - first.ServerTick) / (next.ServerTick - first.ServerTick), 0, 1);
                x = first.X + (next.X - first.X) * blend;
                y = first.Y + (next.Y - first.Y) * blend;
                return true;
            }
            first = next;
        }
        x = last.X; y = last.Y;
        return true;
    }

    public void Reset()
    {
        _snapshots.Clear();
        _receivedAt = 0;
        _renderTick = 0;
    }
}
