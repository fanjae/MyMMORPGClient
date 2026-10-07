using System;
using System.Collections.Generic;

public sealed class MovementReconciliation
{
    public const int MaxHistorySteps = 512;
    private struct PredictedStep
    {
        public ulong Sequence;
        public uint Ordinal;
        public int Horizontal;
        public bool JumpHeld;
        public bool JumpPressed;
    }

    private readonly List<PredictedStep> _history = new();
    private MapGeometryData _geometry;
    private PlatformSimulation _simulation;
    private uint _characterId;
    private PlatformState _state;
    private uint _ordinal;
    private double _accumulator;
    private ulong _lastTick;
    private ulong _ackSequence;
    private uint _ackTicks;
    private ulong _resyncSequence;
    private ulong _pendingWireSequence;
    private bool _jumpPending;
    private bool _jumpHeld;
    private int _horizontal;

    public PlatformState State => _state;
    public bool Ready { get; private set; }
    public bool WaitingForFreshInput { get; private set; }
    public ulong Sequence { get; private set; }
    public int PendingSteps => _history.Count;
    public bool WireJump => _jumpHeld || _jumpPending;
    public int Horizontal => _horizontal;
    public int LastReplayedSteps { get; private set; }
    public double LastCorrection { get; private set; }
    public double LastResetCorrection { get; private set; }
    public double MaxCorrection { get; private set; }

    public void Configure(MapGeometryData geometry, uint characterId)
    {
        Reset();
        if (geometry.Mode != MovementMode.Platformer)
            return;
        _geometry = geometry;
        _characterId = characterId;
        _simulation = new PlatformSimulation(geometry);
        _simulation.Reset(ref _state);
    }

    public void SetInput(int horizontal, bool jumpHeld, bool jumpPressed = false)
    {
        if (horizontal < -1 || horizontal > 1)
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        if (jumpPressed || (jumpHeld && !_jumpHeld))
            _jumpPending = true;
        _horizontal = horizontal;
        _jumpHeld = jumpHeld;
    }

    public void StopInput()
    {
        _horizontal = 0;
        _jumpHeld = false;
        _jumpPending = false;
        _pendingWireSequence = 0;
    }

    public MovementInputData CreateInput()
    {
        if (_geometry == null)
            throw new InvalidOperationException("Movement geometry is not configured.");
        if (Sequence == ulong.MaxValue)
            throw new InvalidOperationException("Movement sequence exhausted.");
        ++Sequence;
        _ordinal = 0;
        if (_jumpPending)
            _pendingWireSequence = Sequence;
        return new MovementInputData
        {
            MapId = _geometry.MapId, Generation = _geometry.Generation, Sequence = Sequence,
            Horizontal = (sbyte)_horizontal, JumpHeld = WireJump
        };
    }

    public int Advance(double elapsedSeconds)
    {
        if (!Ready || WaitingForFreshInput)
            return 0;
        if (double.IsNaN(elapsedSeconds) || double.IsInfinity(elapsedSeconds) || elapsedSeconds < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsedSeconds));
        // 프레임 지연은 최대 5단계까지만 진행하고 수신 때 남은 누적 시간은 버리지 않는다.
        _accumulator = Math.Min(_accumulator + elapsedSeconds, PlatformSimulation.FixedDeltaSeconds * 5);
        int count = 0;
        while (_accumulator + 1e-9 >= PlatformSimulation.FixedDeltaSeconds)
        {
            if (_history.Count >= MaxHistorySteps)
            {
                // 기록이 잘린 상태로 재실행하지 않고 새로운 입력의 서버 확인을 기다린다.
                _history.Clear();
                WaitingForFreshInput = true;
                _resyncSequence = Sequence;
                _accumulator = 0;
                return count;
            }
            PredictedStep step = new()
            {
                Sequence = Sequence, Ordinal = ++_ordinal, Horizontal = _horizontal,
                JumpHeld = _jumpHeld, JumpPressed = _jumpPending
            };
            Step(step);
            _history.Add(step);
            _jumpPending = false;
            _pendingWireSequence = 0;
            _accumulator = Math.Max(0, _accumulator - PlatformSimulation.FixedDeltaSeconds);
            ++count;
        }
        return count;
    }

    public bool Apply(MovementSnapshot snapshot)
    {
        if (_geometry == null || snapshot.MapId != _geometry.MapId || snapshot.Generation != _geometry.Generation ||
            snapshot.CharacterId != _characterId || snapshot.Sequence > Sequence ||
            (Ready && (snapshot.ServerTick < _lastTick || snapshot.Sequence < _ackSequence ||
                (snapshot.Sequence == _ackSequence && snapshot.InputTicks < _ackTicks) ||
                (snapshot.ServerTick == _lastTick && snapshot.Reason == MovementStateReason.Normal))))
            return false;

        PlatformState previous = _state;
        _lastTick = snapshot.ServerTick;
        _ackSequence = snapshot.Sequence;
        _ackTicks = snapshot.InputTicks;
        _state = new PlatformState
        {
            X = snapshot.X, Y = snapshot.Y, VelocityX = snapshot.VelocityX, VelocityY = snapshot.VelocityY,
            FootholdId = snapshot.FootholdId, Grounded = snapshot.Grounded, JumpHeld = snapshot.JumpHeld
        };
        LastResetCorrection = Distance(previous, _state);
        LastReplayedSteps = 0;
        if (snapshot.Reason == MovementStateReason.Respawned)
        {
            _history.Clear();
            _jumpPending = false;
            _pendingWireSequence = 0;
        }
        else
        {
            // 적용된 입력과 해당 입력에서 확인된 단계만 제거해 같은 단계를 다시 적용하지 않는다.
            _history.RemoveAll(step => step.Sequence < snapshot.Sequence ||
                (step.Sequence == snapshot.Sequence && step.Ordinal <= snapshot.InputTicks));
        }
        if (WaitingForFreshInput)
        {
            if (snapshot.Sequence > _resyncSequence && snapshot.InputTicks > 0)
                WaitingForFreshInput = false;
        }
        if (!WaitingForFreshInput)
        {
            foreach (PredictedStep step in _history)
            {
                Step(step);
                ++LastReplayedSteps;
            }
        }
        if (snapshot.Sequence == Sequence)
            _ordinal = Math.Max(_ordinal, snapshot.InputTicks);
        if (_pendingWireSequence != 0 && snapshot.Sequence >= _pendingWireSequence && snapshot.InputTicks > 0)
        {
            _jumpPending = false;
            _pendingWireSequence = 0;
        }
        LastCorrection = Distance(previous, _state);
        MaxCorrection = Math.Max(MaxCorrection, LastCorrection);
        Ready = true;
        return true;
    }

    private void Step(PredictedStep step)
    {
        // 송신과 물리 단계 사이의 짧은 점프도 서버의 pending 입력처럼 한 번만 적용한다.
        if (step.JumpPressed)
            _state.JumpHeld = false;
        _simulation.Step(ref _state, step.Horizontal, step.JumpHeld || step.JumpPressed);
        _state.JumpHeld = step.JumpHeld;
    }

    private static double Distance(PlatformState a, PlatformState b)
    {
        double x = a.X - b.X, y = a.Y - b.Y;
        return Math.Sqrt(x * x + y * y);
    }

    public void Reset()
    {
        _history.Clear();
        _geometry = null;
        _simulation = null;
        _characterId = 0;
        _state = default;
        _ordinal = 0;
        _accumulator = 0;
        _lastTick = 0;
        _ackSequence = 0;
        _ackTicks = 0;
        _resyncSequence = 0;
        _pendingWireSequence = 0;
        _jumpPending = false;
        _jumpHeld = false;
        _horizontal = 0;
        Sequence = 0;
        Ready = false;
        WaitingForFreshInput = false;
        LastReplayedSteps = 0;
        LastCorrection = 0;
        LastResetCorrection = 0;
        MaxCorrection = 0;
    }
}
