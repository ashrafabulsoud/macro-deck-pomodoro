using System.Globalization;
using MacroDeck.Localization;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Actions;
using MacroDeck.Sdk.Events;
using MacroDeck.Sdk.Storage;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Config;
using MacroDeck.Ui.Model.Surfaces;
using MacroDeck.Ui.Runtime;
using MacroDeck.Ui.Widgets;
using Pomodoro.Timer;
using Pomodoro.Widget;
using Serilog;

namespace Pomodoro;

/// <summary>
/// Offers the Pomodoro widget type, draws its tiles, and turns timer transitions into events, desktop
/// notifications and saved state.
/// </summary>
public sealed class PluginIntegration : IPluginIntegration, IWidgetTypeProvider, IUiProvider, IEventProvider
{
	public const string WidgetTypeLocalId = "pomodoro";
	public const string StorageKey = "timers.json";

	private const string DefaultData = """
		{"focusMinutes":25,"shortBreakMinutes":5,"longBreakMinutes":15,"roundsBeforeLongBreak":4,
		"autoStartNext":false,"notifyOnPhaseEnd":false}
		""";

	// The appearance keys stay loose: Macro Deck only offers a property when a plain sample value validates.
	private const string DataSchema = """
		{
		  "type": "object",
		  "properties": {
		    "focusMinutes": { "type": "number", "minimum": 1, "maximum": 180 },
		    "shortBreakMinutes": { "type": "number", "minimum": 1, "maximum": 180 },
		    "longBreakMinutes": { "type": "number", "minimum": 1, "maximum": 180 },
		    "roundsBeforeLongBreak": { "type": "number", "minimum": 1, "maximum": 12 },
		    "autoStartNext": { "type": "boolean" },
		    "notifyOnPhaseEnd": { "type": "boolean" },
		    "border": { "type": "object" },
		    "backgroundColor": { "type": "string" },
		    "accentColor": { "type": "string" },
		    "label": { "type": "string" }
		  }
		}
		""";

	private readonly ILogger _logger;
	private readonly PomodoroTimers _timers;
	private readonly LocalizationResolver _localizer;
	private readonly Lock _sync = new();

	private IIntegrationContext? _context;
	private volatile string? _widgetType;
	private bool _subscribed;
	private bool _loaded;
	private volatile bool _canSave;
	private int _saveRequested;
	private Task _saving = Task.CompletedTask;

	public PluginIntegration(ILogger logger, PomodoroTimers timers)
	{
		_logger = logger.ForContext<PluginIntegration>();
		_timers = timers;

		var catalogs = new LocalizationCatalogRegistry();
		catalogs.Register(Strings.LocalizationCatalog);
		_localizer = new LocalizationResolver(catalogs);
	}

	public IReadOnlyList<IActionDefinition> Actions { get; } = [];

	public IReadOnlyList<EventDefinition> EventDefinitions => PomodoroEvents.Definitions;

	public IReadOnlyList<UiSurfaceDeclaration> Surfaces { get; } =
	[
		new() { Kind = UiSurfaceKinds.Widget, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Preview, SessionMode = UiSessionModes.Shared },
		new() { Kind = UiSurfaceKinds.Config, SessionMode = UiSessionModes.Exclusive }
	];

	/// <summary>
	/// Runs once the session is established, and again after a non-resume reconnect or a configuration
	/// change: the saved timers are read only the first time, and the store keeps running across reconnects.
	/// </summary>
	public async Task InitializeAsync(IIntegrationContext context)
	{
		ArgumentNullException.ThrowIfNull(context);

		lock (_sync)
		{
			_context = context;

			if (!_subscribed)
			{
				_timers.UseWidgetDirectory(KnownWidgets);
				_timers.Transitioned += OnTransitioned;
				_timers.Updated += OnUpdated;
				_subscribed = true;
			}
		}

		if (_loaded)
		{
			return;
		}

		var read = await context.Storage.ReadAsync(StorageKey, PomodoroTimers.IsReadable).ConfigureAwait(false);

		// An unreadable document is left alone rather than overwritten, so it can still be recovered by hand.
		_canSave = read.Status != IntegrationStorageReadStatus.Unreadable;

		if (read.Status == IntegrationStorageReadStatus.Found && read.Content is { } json)
		{
			_timers.Import(json);
		}
		else if (read.Status == IntegrationStorageReadStatus.Unreadable)
		{
			_logger.Warning("The saved Pomodoro timers could not be read; starting fresh without saving");
		}

		_loaded = true;
	}

	public async Task InitializeAsync(IWidgetTypeProviderContext context, CancellationToken cancellationToken = default)
	{
		ArgumentNullException.ThrowIfNull(context);

		var registration = await context.RegisterWidgetTypeAsync(
			new WidgetTypeDescriptor(
				WidgetTypeLocalId,
				Strings.Widget.Name(),
				Strings.Widget.Description(),
				DefaultData,
				DataSchema,
				HasConfiguration: true)
			{
				AppearanceProperties =
				[
					WidgetAppearanceProperty.BackgroundColor,
					WidgetAppearanceProperty.AccentColor,
					WidgetAppearanceProperty.Label
				]
			},
			cancellationToken).ConfigureAwait(false);

		_widgetType = registration.WidgetTypeId;
	}

	public Task<IUiSession?> CreateSessionAsync(UiSessionRequest request, CancellationToken cancellationToken)
	{
		ArgumentNullException.ThrowIfNull(request);

		return Task.FromResult(CreateSession(request.Surface));
	}

	public async Task ShutdownAsync()
	{
		lock (_sync)
		{
			if (_subscribed)
			{
				_timers.Transitioned -= OnTransitioned;
				_timers.Updated -= OnUpdated;
				_subscribed = false;
			}
		}

		Interlocked.Exchange(ref _saveRequested, 1);
		await QueueSave().ConfigureAwait(false);
	}

	// Null until the host has pushed its widget list, so nothing is pruned on a guess.
	private IReadOnlySet<Guid>? KnownWidgets()
	{
		if (Volatile.Read(ref _context) is not { } context)
		{
			return null;
		}

		var widgets = context.Widgets.GetWidgets();

		if (widgets.Count == 0)
		{
			return null;
		}

		var known = new HashSet<Guid>();

		foreach (var widget in widgets)
		{
			if (Guid.TryParse(widget.Id, out var widgetId))
			{
				known.Add(widgetId);
			}
		}

		return known;
	}

	private IUiSession? CreateSession(UiSurface surface)
	{
		if (_widgetType is not { } widgetType)
		{
			return null;
		}

		if (UiWidgetSurface.IsConfigurationFor(surface, widgetType))
		{
			return new ViewSession(new UiView(surface, PomodoroConfigView.Build(UiWidgetSurface.Data(surface))));
		}

		if (!UiWidgetSurface.IsFor(surface, widgetType))
		{
			return null;
		}

		var data = UiWidgetSurface.Data(surface);
		var settings = PomodoroSettings.Parse(data);

		if (surface.Kind == UiSurfaceKinds.Widget && UiWidgetSurface.WidgetId(surface) is { } widgetId)
		{
			return new PomodoroWidgetSession(surface, widgetId, settings, _timers);
		}

		// A picker sample or an editor draft: a fresh focus phase drawn from the draft's settings, never live state.
		var face = PomodoroFace.From(PomodoroState.Initial(_timers.Now, _timers.Today), settings, _timers.Now,
			_timers.Today);

		return new ViewSession(new UiView(surface, PomodoroWidgetView.Build(new UiState<PomodoroFace>(face),
			UiWidgetAppearance.Read(data), UiWidgetSafeArea.RadiusOf(surface))));
	}

	private void OnTransitioned(object? sender, PomodoroTransitionEventArgs args)
	{
		if (Volatile.Read(ref _context) is not { } context)
		{
			return;
		}

		PomodoroEvents.Publish(context.Events, args);

		if (args.Settings.NotifyOnPhaseEnd && args.Transition.Kind == PomodoroTransitionKind.PhaseFinished)
		{
			_ = NotifyAsync(context, args.Transition);
		}
	}

	private async Task NotifyAsync(IIntegrationContext context, PomodoroTransition transition)
	{
		var culture = CultureInfo.CurrentUICulture.Name;
		var focusDone = transition.Phase == PomodoroPhase.Focus;
		var title = focusDone ? Strings.Notifications.FocusDone.Title() : Strings.Notifications.BreakDone.Title();
		var message = focusDone
			? Strings.Notifications.FocusDone.Message(transition.CompletedToday)
			: Strings.Notifications.BreakDone.Message();

		try
		{
			await context.DesktopNotifications
				.ShowAsync(_localizer.Resolve(title, culture), _localizer.Resolve(message, culture))
				.ConfigureAwait(false);
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "Could not show the Pomodoro desktop notification");
		}
	}

	private void OnUpdated(object? sender, EventArgs args)
	{
		// Coalesces bursts: a save already queued picks up every change made before it runs.
		if (Interlocked.Exchange(ref _saveRequested, 1) == 0)
		{
			_ = QueueSave();
		}
	}

	// Saves run one after another, so an older snapshot never overwrites a newer one.
	private Task QueueSave()
	{
		lock (_sync)
		{
			_saving = _saving.ContinueWith(_ => SaveAsync(), CancellationToken.None,
				TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
			return _saving;
		}
	}

	private async Task SaveAsync()
	{
		if (Interlocked.Exchange(ref _saveRequested, 0) == 0
			|| !_canSave
			|| Volatile.Read(ref _context) is not { } context)
		{
			return;
		}

		try
		{
			await context.Storage.WriteAsync(StorageKey, _timers.Export()).ConfigureAwait(false);
		}
		catch (InvalidOperationException)
		{
			// A plugin started without a data directory (a development run) has no storage; that will not
			// change while the process lives, so stop trying instead of warning on every change.
			_canSave = false;
			_logger.Information("No plugin storage is available; Pomodoro timers will not survive a restart");
		}
		catch (Exception exception) when (exception is not OutOfMemoryException)
		{
			_logger.Warning(exception, "Could not save the Pomodoro timers");
		}
	}
}
