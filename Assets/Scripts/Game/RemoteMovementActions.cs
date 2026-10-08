using System;
using System.Collections.Generic;

// 상대도 같은 클라이언트 물리로 행동을 재현한다. LERP는 PlayerView의 표시 위치에만 적용한다.
public sealed class RemoteMovementActions
{
    public const double InterpolationSeconds = 0.2;
    public const double MaxSilenceSeconds = 2;
    private readonly Queue<MovementActionData> _actions = new();
    private PlatformSimulation _simulation;
    private PlatformState _state;
    private ulong _tick, _lastSequence, _lastActionTick;
    private double _sourceTickAtReceive, _receivedAt;
    private int _horizontal;
    private bool _hasRelay;
    public bool Ready => _simulation != null;
    public PlatformState State => _state;
    public bool Teleported { get; private set; }

    public void Configure(MapGeometryData geometry, MovementSnapshot snapshot, double now)
    {
        Reset();
        _simulation = new PlatformSimulation(geometry);
        _state = new PlatformState
        {
            X = snapshot.X, Y = snapshot.Y, VelocityX = snapshot.VelocityX, VelocityY = snapshot.VelocityY,
            Grounded = snapshot.Grounded, FootholdId = snapshot.FootholdId
        };
        _tick = _lastActionTick = snapshot.ServerTick;
        _lastSequence = snapshot.Sequence;
        _horizontal = Math.Sign(snapshot.VelocityX);
        _sourceTickAtReceive = _tick;
        _receivedAt = now;
    }

    public bool Add(RelayedMovementAction relay, double now)
    {
        if (!Ready || relay.Action.Sequence <= _lastSequence || relay.Action.ClientTick < _lastActionTick)
            return false;
        _lastSequence = relay.Action.Sequence;
        _lastActionTick = relay.Action.ClientTick;
        // 최초 행동 전에는 캐릭터의 로컬 tick이 아직 진행되지 않았을 수 있다.
        _sourceTickAtReceive = _hasRelay ? Math.Max(SourceTick(now), relay.LatestClientTick) : relay.LatestClientTick;
        _receivedAt = now;
        _hasRelay = true;
        if (_actions.Count >= 128)
            throw new InvalidOperationException("Remote movement action queue limit reached");
        _actions.Enqueue(relay.Action);
        return true;
    }

    public bool Sample(double now, out double x, out double y)
    {
        x = y = 0;
        if (!Ready)
            return false;
        Teleported = false;
        double target = Math.Max(_tick, SourceTick(now) - InterpolationSeconds / PlatformSimulation.FixedDeltaSeconds);
        int steps = 0;
        while (true)
        {
            while (_actions.Count > 0 && _actions.Peek().ClientTick <= _tick)
            {
                MovementActionData action = _actions.Dequeue();
                _state = action.State;
                _state.JumpHeld = false;
                _horizontal = action.Horizontal;
                Teleported |= action.Kind == MovementActionKind.Respawn;
                // 늦은 이벤트는 그 기준 상태에서 현재 표시 단계까지 따라잡는다.
                ulong age = _tick - action.ClientTick;
                for (ulong i = 0; i < Math.Min(age, 100UL); ++i)
                    _simulation.Step(ref _state, _horizontal, false);
            }
            if (_tick + 1 > target || ++steps > 100)
                break;
            _simulation.Step(ref _state, _horizontal, false);
            _state.JumpHeld = false;
            ++_tick;
        }
        x = _state.X; y = _state.Y;
        return true;
    }

    private double SourceTick(double now) => _sourceTickAtReceive +
        (_hasRelay ? Math.Clamp(now - _receivedAt, 0, MaxSilenceSeconds) / PlatformSimulation.FixedDeltaSeconds : 0);

    public void Reset()
    {
        _actions.Clear(); _simulation = null; _state = default;
        _tick = _lastSequence = _lastActionTick = 0;
        _sourceTickAtReceive = _receivedAt = 0;
        _horizontal = 0; _hasRelay = Teleported = false;
    }
}
