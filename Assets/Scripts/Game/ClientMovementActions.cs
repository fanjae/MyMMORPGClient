using System;
using System.Collections.Generic;

// 본인 클라이언트만 물리와 착지를 결정한다. 송신 주기와 물리 주기는 독립적이다.
public sealed class ClientMovementActions
{
    public const double SendIntervalSeconds = 0.2;
    public const double CheckpointSeconds = 1;
    private readonly Queue<MovementActionData> _pending = new();
    private MapGeometryData _geometry;
    private PlatformSimulation _simulation;
    private PlatformState _state;
    private double _accumulator, _nextSendAt, _checkpointAt;
    private int _horizontal;
    private bool _inputChanged, _jumpPending, _airborne;
    private ulong _sequence, _batchSequence, _jumpId;

    public bool Ready => _simulation != null;
    public PlatformState State => _state;
    public ulong Tick { get; private set; }
    public ulong JumpId => _jumpId;
    public bool CanJump => Ready && _state.Grounded && !_airborne && !_jumpPending;
    public int PendingActions => _pending.Count;
    public ulong PacketsSent { get; private set; }
    public double NextSendAt => _nextSendAt;

    public void Configure(MapGeometryData geometry)
    {
        Reset();
        if (geometry?.Mode != MovementMode.Platformer)
            return;
        _geometry = geometry;
        _simulation = new PlatformSimulation(geometry);
        _simulation.Reset(ref _state);
        _inputChanged = true;
    }

    public void SetInput(int horizontal, bool jumpPressed)
    {
        if (horizontal < -1 || horizontal > 1)
            throw new ArgumentOutOfRangeException(nameof(horizontal));
        if (_horizontal != horizontal)
        {
            _horizontal = horizontal;
            _inputChanged = true;
        }
        // 공중 입력은 예약하지 않아 착지 직후 지연된 재점프도 발생하지 않는다.
        if (jumpPressed && CanJump)
            _jumpPending = true;
    }

    public void Advance(double elapsed)
    {
        if (!Ready)
            return;
        if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0)
            throw new ArgumentOutOfRangeException(nameof(elapsed));
        _accumulator = Math.Min(_accumulator + elapsed, PlatformSimulation.FixedDeltaSeconds * 5);
        while (_accumulator + 1e-9 >= PlatformSimulation.FixedDeltaSeconds)
        {
            PlatformState before = _state;
            bool jump = _jumpPending && !_airborne && before.Grounded;
            _jumpPending = false;
            _simulation.Step(ref _state, _horizontal, jump);
            _state.JumpHeld = false; // 눌림 이벤트 한 번만 적용하며 키 유지로 재점프하지 않는다.
            ++Tick;
            if (jump || (before.Grounded && !_state.Grounded))
            {
                _airborne = true;
                ++_jumpId;
                Enqueue(jump ? MovementActionKind.Jump : MovementActionKind.Fall);
            }
            else if (_airborne && _state.Grounded)
            {
                _airborne = false;
                Enqueue(_simulation.LastRespawned ? MovementActionKind.Respawn : MovementActionKind.Land);
            }
            else if (_inputChanged)
                Enqueue(MovementActionKind.Input);
            _inputChanged = false;
            _accumulator = Math.Max(0, _accumulator - PlatformSimulation.FixedDeltaSeconds);
        }
    }

    public void StopInput()
    {
        SetInput(0, false);
        _jumpPending = false;
    }

    public bool TryCreateBatch(double now, out MovementActionBatch batch)
    {
        batch = null;
        if (!Ready || now + 1e-9 < _nextSendAt)
            return false;
        if (_pending.Count == 0)
        {
            if (now < _checkpointAt)
                return false;
            Enqueue(MovementActionKind.Checkpoint);
        }
        batch = new MovementActionBatch
        {
            MapId = _geometry.MapId, Generation = _geometry.Generation,
            BatchSequence = ++_batchSequence, LatestClientTick = Tick
        };
        while (_pending.Count > 0 && batch.Actions.Count < MovementActionProtocol.MaxActions)
            batch.Actions.Add(_pending.Dequeue());
        _nextSendAt = now + SendIntervalSeconds;
        _checkpointAt = now + CheckpointSeconds;
        ++PacketsSent;
        return true;
    }

    private void Enqueue(MovementActionKind kind)
    {
        if (_pending.Count >= 128)
            throw new InvalidOperationException("Movement action queue limit reached");
        _pending.Enqueue(new MovementActionData
        {
            Sequence = ++_sequence, ClientTick = Tick, JumpId = _jumpId,
            State = _state, Horizontal = _horizontal, Kind = kind
        });
    }

    public void Reset()
    {
        _geometry = null; _simulation = null; _state = default;
        _pending.Clear(); _accumulator = _checkpointAt = 0;
        _horizontal = 0; _inputChanged = _jumpPending = _airborne = false;
        _sequence = _batchSequence = _jumpId = Tick = 0;
        // 맵 전환 직후에도 동일 연결의 200ms 송신 간격은 유지한다.
    }
}
