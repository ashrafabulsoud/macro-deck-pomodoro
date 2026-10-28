using System.Text.Json;
using MacroDeck.Plugin.Testing;
using MacroDeck.Plugin.Testing.Fakes;
using MacroDeck.Sdk;
using MacroDeck.Sdk.Ui;
using MacroDeck.Sdk.Widgets;
using MacroDeck.Ui.Model.Surfaces;
using Microsoft.Extensions.DependencyInjection;
using NUnit.Framework;
using Pomodoro.Timer;

namespace Pomodoro.Tests;

/// <summary>Behaviour through <see cref="PluginTestHarness" />: the real integration, a fake host and a manual clock.</summary>
[TestFixture]
public sealed class PluginIntegrationTests
{
	private static readonly Guid _widgetId = Guid.Parse("5d0c7a52-2f4e-4a8e-9a43-3b1f5c2a9e10");

	private static PluginTestHarness CreateHarness() =>
		PluginTestHarness.Create(builder =>
		{
			builder.Services.AddSingleton<PomodoroTimers>();
			builder
				.UseLocalization(Strings.LocalizationCatalog)
				.RegisterIntegration<PluginIntegration>();
		});

	private static PluginIntegration IntegrationOf(PluginTestHarness harness) =>
		harness.Services.GetServices<IPluginIntegration>().OfType<PluginIntegration>().Single();

	private static PomodoroTimers TimersOf(PluginTestHarness harness) =>
		harness.Services.GetRequiredService<PomodoroTimers>();

	private static PomodoroSettings OneMinuteFocus(bool notify = false) =>
		PomodoroSettings.Default with { FocusMinutes = 1, NotifyOnPhaseEnd = notify, Label = "Desk" };

	[Test]
	public async Task The_plugin_builds_and_initializes()
	{
		await using var harness = CreateHarness();

		Assert.DoesNotThrowAsync(harness.InitializeIntegrationsAsync);
	}

	[Test]
	public async Task The_widget_type_is_registered_as_configurable_with_appearance()
	{
		await using var harness = CreateHarness();
		var context = new FakeWidgetTypeProviderContext();

		await IntegrationOf(harness).InitializeAsync(context);

		var widgetType = context.WidgetTypes[PluginIntegration.WidgetTypeLocalId];
		Assert.That(widgetType.HasConfiguration, Is.True);
		Assert.That(widgetType.AppearanceProperties, Does.Contain(WidgetAppearanceProperty.Label));
		Assert.That(PomodoroSettings.Parse(JsonDocument.Parse(widgetType.DefaultData!).RootElement),
			Is.EqualTo(PomodoroSettings.Default));
	}

	[Test]
	public async Task A_finished_phase_publishes_events_naming_the_widget()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);

		timers.Configure(_widgetId, OneMinuteFocus());
		timers.Press(_widgetId);
		harness.Clock.Advance(TimeSpan.FromMinutes(1));

		var ids = harness.Context.Events.Published.Select(e => e.EventId).ToList();
		Assert.That(ids, Is.EqualTo(new[]
		{
			PomodoroEvents.PhaseStartedId, PomodoroEvents.PhaseFinishedId, PomodoroEvents.PomodoroCompletedId
		}));

		var finished = harness.Context.Events.Published[1].Parameters!.Value;
		Assert.That(finished.GetProperty(PomodoroEvents.PhaseParameter).GetString(), Is.EqualTo("focus"));
		Assert.That(finished.GetProperty(PomodoroEvents.LabelParameter).GetString(), Is.EqualTo("Desk"));
		Assert.That(finished.GetProperty(PomodoroEvents.WidgetIdParameter).GetString(),
			Is.EqualTo(_widgetId.ToString()));
	}

	[Test]
	public async Task Nothing_finishes_before_the_phase_has_run_out()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);

		timers.Configure(_widgetId, OneMinuteFocus());
		timers.Press(_widgetId);
		harness.Clock.Advance(TimeSpan.FromSeconds(59));

		Assert.That(harness.Context.Events.Published.Select(e => e.EventId),
			Does.Not.Contain(PomodoroEvents.PhaseFinishedId));
		Assert.That(timers.Get(_widgetId).Status, Is.EqualTo(PomodoroStatus.Running));
	}

	[Test]
	public async Task A_desktop_notification_is_shown_only_when_enabled()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);
		var quiet = Guid.NewGuid();

		timers.Configure(_widgetId, OneMinuteFocus(notify: true));
		timers.Configure(quiet, OneMinuteFocus(notify: false));
		timers.Press(_widgetId);
		timers.Press(quiet);
		harness.Clock.Advance(TimeSpan.FromMinutes(1));

		var shown = harness.Context.DesktopNotifications.Shown.Single();
		Assert.That(shown.Title, Is.EqualTo("Focus done"));
		Assert.That(shown.Message, Does.Contain("1 done today"));
	}

	[Test]
	public async Task A_running_timer_survives_a_restart()
	{
		string saved;

		await using (var first = CreateHarness())
		{
			await first.InitializeIntegrationsAsync();
			TimersOf(first).Configure(_widgetId, OneMinuteFocus());
			TimersOf(first).Press(_widgetId);
			await IntegrationOf(first).ShutdownAsync();
			saved = first.Context.Storage.Get(PluginIntegration.StorageKey)!;
		}

		await using var second = CreateHarness();
		second.Context.Storage.Set(PluginIntegration.StorageKey, saved);
		await second.InitializeIntegrationsAsync();

		var restored = TimersOf(second).Snapshot(_widgetId);
		Assert.That(restored.State.IsRunning, Is.True);
		Assert.That(restored.Settings.Label, Is.EqualTo("Desk"));
	}

	[Test]
	public async Task An_unreadable_save_is_left_untouched()
	{
		await using var harness = CreateHarness();
		harness.Context.Storage.SetUnreadable(PluginIntegration.StorageKey);
		await harness.InitializeIntegrationsAsync();

		TimersOf(harness).Press(_widgetId);
		await IntegrationOf(harness).ShutdownAsync();

		Assert.That(harness.Context.Storage.Get(PluginIntegration.StorageKey), Is.Null);
	}

	[Test]
	public async Task A_deleted_widget_stops_firing_events()
	{
		await using var harness = CreateHarness();
		harness.Context.Widgets.Seed(new WidgetTargetInfo { Id = Guid.NewGuid().ToString(), Label = "Other", Location = "Home", Type = "test-provider::pomodoro" });
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);

		timers.Configure(_widgetId, PomodoroSettings.Default with { AutoStartNext = true, FocusMinutes = 5 });
		timers.Press(_widgetId);

		for (var i = 0; i < 24; i++)
		{
			harness.Clock.Advance(TimeSpan.FromSeconds(5));
		}

		var published = harness.Context.Events.Published.Count;
		harness.Clock.Advance(TimeSpan.FromMinutes(30));

		Assert.That(harness.Context.Events.Published, Has.Count.EqualTo(published));
		Assert.That(TimersOf(harness).Export(), Does.Not.Contain(_widgetId.ToString()));
	}

	[Test]
	public async Task A_widget_the_host_lists_keeps_running()
	{
		await using var harness = CreateHarness();
		harness.Context.Widgets.Seed(new WidgetTargetInfo { Id = _widgetId.ToString(), Label = "Desk", Location = "Home", Type = "test-provider::pomodoro" });
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);

		timers.Configure(_widgetId, OneMinuteFocus());
		timers.Press(_widgetId);
		harness.Clock.Advance(TimeSpan.FromMinutes(1));

		Assert.That(harness.Context.Events.Published.Select(e => e.EventId),
			Does.Contain(PomodoroEvents.PomodoroCompletedId));
	}

	[Test]
	public async Task Importing_again_leaves_widgets_this_process_already_knows()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);
		timers.Configure(_widgetId, OneMinuteFocus());
		var saved = timers.Export();

		timers.Press(_widgetId);
		timers.Import(saved);

		Assert.That(timers.Get(_widgetId).IsRunning, Is.True);
	}

	[Test]
	public async Task Tiles_refresh_at_midnight_so_the_daily_count_starts_over()
	{
		await using var harness = CreateHarness();
		await harness.InitializeIntegrationsAsync();
		var timers = TimersOf(harness);
		timers.Configure(_widgetId, OneMinuteFocus());
		timers.Press(_widgetId);
		harness.Clock.Advance(TimeSpan.FromMinutes(1));
		Assert.That(timers.Get(_widgetId).CompletedOn(timers.Today), Is.EqualTo(1));

		var refreshed = 0;
		timers.Changed += (_, args) => refreshed += args.WidgetId == _widgetId ? 1 : 0;
		harness.Clock.Advance(TimeSpan.FromDays(1));

		Assert.That(refreshed, Is.GreaterThan(0));
		Assert.That(timers.Get(_widgetId).CompletedOn(timers.Today), Is.Zero);
	}

	[Test]
	public async Task Surfaces_of_another_widget_type_are_declined()
	{
		await using var harness = CreateHarness();
		var integration = IntegrationOf(harness);
		await integration.InitializeAsync(new FakeWidgetTypeProviderContext());

		var session = await integration.CreateSessionAsync(Request("someone.else::clock", _widgetId), default);

		Assert.That(session, Is.Null);
	}

	[Test]
	public async Task A_placed_widget_gets_a_live_session_and_a_tap_starts_it()
	{
		await using var harness = CreateHarness();
		var integration = IntegrationOf(harness);
		await harness.InitializeIntegrationsAsync();
		await integration.InitializeAsync(new FakeWidgetTypeProviderContext());

		await using var session = await integration.CreateSessionAsync(
			Request(PluginIntegration.WidgetTypeLocalId, _widgetId), default);

		Assert.That(session, Is.Not.Null);
		Assert.That(session!.BuildTree(), Is.Not.Null);

		TimersOf(harness).Press(_widgetId);
		Assert.That(session.DrainPatches(), Is.Not.Empty);
	}

	private static UiSessionRequest Request(string widgetType, Guid widgetId)
	{
		return new UiSessionRequest
		{
			UiModelVersion = 1,
			Surface = new UiSurface
			{
				Kind = UiSurfaceKinds.Widget,
				SessionMode = UiSessionModes.Shared,
				Attributes = new Dictionary<string, JsonElement>
				{
					[UiWidgetSurfaceAttributes.WidgetType] = JsonSerializer.SerializeToElement(widgetType),
					[UiWidgetSurfaceAttributes.WidgetId] = JsonSerializer.SerializeToElement(widgetId.ToString()),
					[UiWidgetSurfaceAttributes.Data] = JsonDocument.Parse("{}").RootElement.Clone()
				}
			}
		};
	}
}

/// <summary>
/// The localization set is generated from <c>Localization/*.resx</c>, so these guard the wiring rather than
/// any wording: a missing catalog registration leaves every label showing its raw key.
/// </summary>
[TestFixture]
public sealed class LocalizationTests
{
	[Test]
	public void The_catalog_is_scoped_to_the_plugin_id()
	{
		Assert.That(Strings.LocalizationCatalog.Scope, Is.EqualTo("plugin:com.ashrafabulsoud.pomodoro"));
	}

	[Test]
	public void English_is_the_default_culture()
	{
		Assert.That(Strings.LocalizationCatalog.DefaultCulture, Is.EqualTo("en"));
		Assert.That(Strings.LocalizationCatalog.Cultures, Does.Contain("en"));
	}

	[Test]
	public void Every_key_the_default_culture_declares_resolves_to_text()
	{
		foreach (var key in Strings.LocalizationCatalog.KeysOf("en"))
		{
			Assert.That(Strings.LocalizationCatalog.TryGetTemplate("en", key, out var text), Is.True);
			Assert.That(text, Is.Not.Empty);
		}
	}
}
