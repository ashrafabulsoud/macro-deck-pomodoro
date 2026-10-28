using MacroDeck.Sdk.Ui;
using MacroDeck.Ui.Components;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Dsl;
using MacroDeck.Ui.Model.Events;
using MacroDeck.Ui.Model.Nodes;
using MacroDeck.Ui.Model.Patches;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Pomodoro.Timer;

namespace Pomodoro.Widget;

/// <summary>One placed widget as one viewer sees it. The timer itself lives in <see cref="PomodoroTimers" />.</summary>
internal sealed class PomodoroWidgetSession : IUiSession
{
	private readonly Guid _widgetId;
	private readonly PomodoroTimers _timers;
	private readonly UiState<PomodoroFace> _face;
	private readonly UiView _view;
	private readonly Lock _sync = new();

	private Action? _pendingGesture;
	private bool _disposed;

	public PomodoroWidgetSession(UiSurface surface, Guid widgetId, PomodoroSettings settings, PomodoroTimers timers)
	{
		ArgumentNullException.ThrowIfNull(surface);

		_widgetId = widgetId;
		_timers = timers;

		// Every save reopens the session with the new data, which is how edited settings reach the timer.
		_timers.Attach(widgetId);
		_timers.Configure(widgetId, settings);
		_face = new UiState<PomodoroFace>(CurrentFace());

		IReadOnlyList<UiEventHandler> events =
		[
			UiEventHandler.On(UiComponentEvents.Press, () => _pendingGesture = () => _timers.Press(_widgetId)),
			UiEventHandler.On(UiComponentEvents.LongPress, () => _pendingGesture = () => _timers.Reset(_widgetId))
		];

		_view = new UiView(surface, PomodoroWidgetView.Build(_face,
			UiWidgetAppearance.Read(UiWidgetSurface.Data(surface)), UiWidgetSafeArea.RadiusOf(surface), events));
		_view.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
		_view.HandlerFaulted += (_, fault)
			=> Faulted?.Invoke(this, new UiSessionFaultedEventArgs(fault.Exception.Message, fault.Exception));

		_timers.Changed += OnTimerChanged;

		// A change between reading the first face and subscribing above is not lost.
		OnTimerChanged(null, new PomodoroChangedEventArgs(_widgetId));
	}

	public event EventHandler? Changed;

	public event EventHandler<UiSessionFaultedEventArgs>? Faulted;

	public UiTree BuildTree()
	{
		lock (_sync)
		{
			return _view.Tree;
		}
	}

	public IReadOnlyList<UiPatch> DrainPatches()
	{
		lock (_sync)
		{
			return _view.DrainPatches();
		}
	}

	public void Dispatch(UiEvent uiEvent)
	{
		Action? gesture;

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			_view.Dispatch(uiEvent);
			gesture = _pendingGesture;
			_pendingGesture = null;
		}

		// Applied outside the lock: the store notifies every session of this widget, and holding this one's
		// lock meanwhile could deadlock against another viewer pressing at the same moment.
		gesture?.Invoke();
	}

	public ValueTask DisposeAsync()
	{
		_timers.Changed -= OnTimerChanged;

		lock (_sync)
		{
			if (_disposed)
			{
				return ValueTask.CompletedTask;
			}

			_disposed = true;
			_timers.Detach(_widgetId);
			_view.Dispose();
		}

		return ValueTask.CompletedTask;
	}

	private void OnTimerChanged(object? sender, PomodoroChangedEventArgs args)
	{
		if (args.WidgetId != _widgetId)
		{
			return;
		}

		lock (_sync)
		{
			if (_disposed)
			{
				return;
			}

			var face = CurrentFace();

			if (face != _face.Value)
			{
				using (_view.Batch())
				{
					_face.Set(face);
				}
			}
		}
	}

	private PomodoroFace CurrentFace()
	{
		var (state, settings) = _timers.Snapshot(_widgetId);

		return PomodoroFace.From(state, settings, _timers.Now, _timers.Today);
	}
}
