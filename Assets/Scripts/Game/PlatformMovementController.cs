using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlatformMovementController : MonoBehaviour
{
    private NetworkManager _network;
    private WorldManager _world;
    private MapGeometryData _geometry;
    private PlatformSimulation _simulation;
    private PlatformState _state;
    private ulong _sequence;
    private ulong _lastTick;
    private bool _hasState;
    private bool _sending;
    private bool _sentJump;
    private int _sentHorizontal;
    private bool _jumpPending;
    private bool _previousJump;
    private double _accumulator;
    private float _nextSendTime;

    public bool InputAllowed { get; set; } = true;
    public bool IsPlatformer => _geometry?.Mode == MovementMode.Platformer;
    public bool GeometryReady => _geometry != null;
    public MovementStateReason LastReason { get; private set; }

    private void Awake()
    {
        _network = GetComponent<NetworkManager>();
        _world = GetComponent<WorldManager>();
    }

    private void OnEnable()
    {
        _network.GeometryReceived += OnGeometry;
        _network.MovementReceived += OnState;
        _network.MapChanged += OnMapChanged;
        _network.GameDisconnected += Reset;
    }

    private void OnDisable()
    {
        _network.GeometryReceived -= OnGeometry;
        _network.MovementReceived -= OnState;
        _network.MapChanged -= OnMapChanged;
        _network.GameDisconnected -= Reset;
    }

    private void OnGeometry(MapGeometryData geometry)
    {
        Reset();
        _geometry = geometry;
        if (IsPlatformer)
        {
            _simulation = new PlatformSimulation(geometry);
            _simulation.Reset(ref _state);
        }
    }

    private void OnState(MovementSnapshot snapshot)
    {
        if (!IsPlatformer || snapshot.CharacterId != _world.LocalCharacterId || snapshot.MapId != _geometry.MapId || snapshot.Generation != _geometry.Generation || (_hasState && snapshot.ServerTick < _lastTick))
            return;

        _lastTick = snapshot.ServerTick;
        _hasState = true;
        LastReason = snapshot.Reason;
        // 서버 상태에서 예측을 다시 시작하고 화면의 작은 오차는 PlayerView에서 보간한다.
        _state.X = snapshot.X;
        _state.Y = snapshot.Y;
        _state.VelocityX = snapshot.VelocityX;
        _state.VelocityY = snapshot.VelocityY;
        _state.FootholdId = snapshot.FootholdId;
        _state.Grounded = snapshot.Grounded;
        _state.JumpHeld = _previousJump;
        _accumulator = 0;
        _world.LocalPlayer?.PredictPosition(_state.X, _state.Y);
    }

    private void Update()
    {
        if (!IsPlatformer || !_hasState || _world.LocalPlayer == null)
            return;

        Keyboard keyboard = Keyboard.current;
        bool allowed = InputAllowed && Application.isFocused && GUIUtility.keyboardControl == 0 && keyboard != null;
        int horizontal = allowed ? (keyboard.rightArrowKey.isPressed ? 1 : 0) - (keyboard.leftArrowKey.isPressed ? 1 : 0) : 0;
        bool jump = allowed && keyboard.spaceKey.isPressed;
        if (allowed && keyboard.spaceKey.wasPressedThisFrame && !_previousJump)
            _jumpPending = true;

        _previousJump = jump;
        if (!allowed)
            _jumpPending = false;

        bool wireJump = jump || _jumpPending;
        if (!_sending && (horizontal != _sentHorizontal || wireJump != _sentJump || Time.unscaledTime >= _nextSendTime))
        {
            SendInput(horizontal, wireJump);
            _jumpPending = false;
        }

        // 과도한 프레임 지연도 서버와 같은 고정 간격 및 최대 5 tick으로 처리한다.
        _accumulator = Math.Min(_accumulator + Time.unscaledDeltaTime, 0.1);
        while (_accumulator >= PlatformSimulation.FixedDeltaSeconds)
        {
            _simulation.Step(ref _state, horizontal, wireJump);
            _accumulator -= PlatformSimulation.FixedDeltaSeconds;
        }

        _world.LocalPlayer.PredictPosition(_state.X, _state.Y);
    }

    private async void SendInput(int horizontal, bool jump)
    {
        MapGeometryData geometry = _geometry;
        MovementInputData input = new() { MapId = geometry.MapId, Generation = geometry.Generation, Sequence = ++_sequence, Horizontal = (sbyte)horizontal, JumpHeld = jump };
        _sentHorizontal = horizontal;
        _sentJump = jump;
        _nextSendTime = Time.unscaledTime + 0.05f;
        _sending = true;
        try
        {
            await _network.SendMovementInputAsync(input);
        }
        catch (Exception exception)
        {
            if (ReferenceEquals(_geometry, geometry))
            {
                _hasState = false;
                Debug.LogException(exception);
            }
        }
        finally
        {
            if (ReferenceEquals(_geometry, geometry))
                _sending = false;
        }
    }

    private void OnMapChanged(ChangeMapData data)
    {
        if (data.Result == ChangeMapResult.Success)
            Reset();
    }

    private void Reset()
    {
        _geometry = null;
        _simulation = null;
        _hasState = false;
        _sending = false;
        _sequence = 0;
        _lastTick = 0;
        _sentHorizontal = 0;
        _sentJump = false;
        _previousJump = false;
        _jumpPending = false;
        _accumulator = 0;
        _nextSendTime = 0;
    }
}
