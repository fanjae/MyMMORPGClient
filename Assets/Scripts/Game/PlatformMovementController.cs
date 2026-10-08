using System;
using UnityEngine;
using UnityEngine.InputSystem;

public sealed class PlatformMovementController : MonoBehaviour
{
    private NetworkManager _network;
    private WorldManager _world;
    private MapGeometryData _geometry;
    private readonly ClientMovementActions _movement = new();
    private bool _sendFailed;
    private bool _sending;

    public bool InputAllowed { get; set; } = true;
    public bool IsPlatformer => _geometry?.Mode == MovementMode.Platformer;
    public bool GeometryReady => _geometry != null;
    public int PendingActions => _movement.PendingActions;
    public ulong PacketsSent => _movement.PacketsSent;
    public bool CanJump => _movement.CanJump;

    private void Awake()
    {
        _network = GetComponent<NetworkManager>();
        _world = GetComponent<WorldManager>();
    }

    private void OnEnable()
    {
        _network.GeometryReceived += OnGeometry;
        _network.MapChanged += OnMapChanged;
        _network.GameDisconnected += Reset;
    }

    private void OnDisable()
    {
        _network.GeometryReceived -= OnGeometry;
        _network.MapChanged -= OnMapChanged;
        _network.GameDisconnected -= Reset;
    }

    private void OnGeometry(MapGeometryData geometry)
    {
        Reset();
        _geometry = geometry;
        if (IsPlatformer)
            _movement.Configure(geometry);
    }

    private void Update()
    {
        if (_sendFailed || !IsPlatformer || !_movement.Ready || _world.LocalPlayer == null)
            return;
        Keyboard keyboard = Keyboard.current;
        bool allowed = InputAllowed && Application.isFocused && GUIUtility.keyboardControl == 0 && keyboard != null;
        if (allowed)
        {
            int horizontal = (keyboard.rightArrowKey.isPressed ? 1 : 0) - (keyboard.leftArrowKey.isPressed ? 1 : 0);
            _movement.SetInput(horizontal, keyboard.spaceKey.wasPressedThisFrame);
        }
        else
        {
            _movement.StopInput();
        }
        try
        {
            _movement.Advance(Time.unscaledDeltaTime);
            if (!_sending && _movement.TryCreateBatch(Time.unscaledTimeAsDouble, out MovementActionBatch batch))
                SendActions(batch);
        }
        catch (Exception exception)
        {
            _sendFailed = true;
            Debug.LogException(exception);
        }
        PlatformState state = _movement.State;
        _world.LocalPlayer.PredictPosition(state.X, state.Y);
    }

    private async void SendActions(MovementActionBatch batch)
    {
        MapGeometryData geometry = _geometry;
        _sending = true;
        try { await _network.SendMovementActionsAsync(batch); }
        catch (Exception exception)
        {
            if (ReferenceEquals(_geometry, geometry))
            {
                _sendFailed = true;
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
        _movement.Reset();
        _sendFailed = false;
        _sending = false;
    }
}
