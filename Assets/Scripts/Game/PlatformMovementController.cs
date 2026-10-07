using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlatformMovementController : MonoBehaviour
{
    private NetworkManager _network;
    private WorldManager _world;
    private MapGeometryData _geometry;
    private readonly MovementReconciliation _prediction = new();
    private bool _sendFailed;
    private bool _sentJump;
    private int _sentHorizontal;
    private float _nextSendTime;

    public bool InputAllowed { get; set; } = true;
    public bool IsPlatformer => _geometry?.Mode == MovementMode.Platformer;
    public bool GeometryReady => _geometry != null;
    public MovementStateReason LastReason { get; private set; }
    public int PendingSteps => _prediction.PendingSteps;
    public int ReplayedSteps => _prediction.LastReplayedSteps;
    public double LastCorrection => _prediction.LastCorrection;
    public bool WaitingForFreshInput => _prediction.WaitingForFreshInput;

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
            _prediction.Configure(geometry, _world.LocalCharacterId);
    }

    private void OnState(MovementSnapshot snapshot)
    {
        if (_sendFailed || !IsPlatformer || !_prediction.Apply(snapshot))
            return;
        LastReason = snapshot.Reason;
        PlatformState state = _prediction.State;
        _world.LocalPlayer?.CorrectPredictedPosition(state.X, state.Y, snapshot.Reason == MovementStateReason.Respawned);
    }

    private void Update()
    {
        if (_sendFailed || !IsPlatformer || !_prediction.Ready || _world.LocalPlayer == null)
            return;
        Keyboard keyboard = Keyboard.current;
        bool allowed = InputAllowed && Application.isFocused && GUIUtility.keyboardControl == 0 && keyboard != null;
        if (allowed)
        {
            int horizontal = (keyboard.rightArrowKey.isPressed ? 1 : 0) - (keyboard.leftArrowKey.isPressed ? 1 : 0);
            _prediction.SetInput(horizontal, keyboard.spaceKey.isPressed, keyboard.spaceKey.wasPressedThisFrame);
        }
        else
        {
            // 채팅 포커스와 창 비활성화의 중립 입력에 이전 짧은 점프를 섞지 않는다.
            _prediction.StopInput();
        }
        if (_prediction.Horizontal != _sentHorizontal || _prediction.WireJump != _sentJump || Time.unscaledTime >= _nextSendTime)
            SendInput();
        _prediction.Advance(Time.unscaledDeltaTime);
        PlatformState state = _prediction.State;
        _world.LocalPlayer.PredictPosition(state.X, state.Y);
    }

    private async void SendInput()
    {
        MapGeometryData geometry = _geometry;
        MovementInputData input = _prediction.CreateInput();
        _sentHorizontal = input.Horizontal;
        _sentJump = input.JumpHeld;
        _nextSendTime = Time.unscaledTime + 0.05f;
        // 송신 중 바뀐 입력도 번호를 부여하고 TcpSession의 직렬 송신 순서를 사용한다.
        try { await _network.SendMovementInputAsync(input); }
        catch (Exception exception)
        {
            if (ReferenceEquals(_geometry, geometry))
            {
                _sendFailed = true;
                Debug.LogException(exception);
            }
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
        _prediction.Reset();
        _sendFailed = false;
        _sentHorizontal = 0;
        _sentJump = false;
        _nextSendTime = 0;
        LastReason = MovementStateReason.Normal;
    }
}
