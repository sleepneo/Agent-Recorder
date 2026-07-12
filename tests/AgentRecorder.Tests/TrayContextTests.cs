using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Capture;
using AgentRecorder.Core;
using AgentRecorder.Infrastructure;
using AgentRecorder.Logging;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-AgentRecorderDataDir")]
public class TrayContextTests : IDisposable
{
    private readonly TempDirectory _tmp = new();

    public TrayContextTests()
    {
        DataDirResolver.SetOverride(_tmp.Path);
    }

    public void Dispose()
    {
        DataDirResolver.ClearOverride();
        _tmp.Dispose();
    }

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception e) { ex = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new TargetInvocationException(ex);
    }

    private static T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(obj)!;
    }

    private static TrayContext CreateContext(UiLanguage language, AuditLogger? audit = null)
    {
        var a = audit ?? new CaptureAuditLogger();
        var engine = new RecordingEngine(a);
        var ctx = new TrayContext(engine, a, FakeGlobalStopHotkeyFactory.Create(), uiTextProvider: new UiTextProvider(language));
        engine.SetTray(ctx);
        return ctx;
    }

    private static void SetLanguage(TrayContext ctx, UiLanguage language)
    {
        var method = typeof(TrayContext).GetMethod("SetLanguage", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method!.Invoke(ctx, new object[] { language });
    }

    [Fact]
    public void Constructor_ZhCnStartup_AllPermanentMenuItemsAreChinese_AndChineseChecked()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(UiLanguage.ZhCn);

            var statusItem = GetPrivateField<ToolStripMenuItem>(ctx, "_statusItem");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");
            var openItem = GetPrivateField<ToolStripMenuItem>(ctx, "_openOutputFolderItem");
            var languageItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageItem");
            var zhItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageZhCnItem");
            var enItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageEnUsItem");
            var exitItem = GetPrivateField<ToolStripMenuItem>(ctx, "_exitItem");

            Assert.Equal("状态：空闲", statusItem.Text);
            Assert.Equal("停止录制", stopItem.Text);
            Assert.Equal("打开输出文件夹", openItem.Text);
            Assert.Equal("语言 / Language", languageItem.Text);
            Assert.Equal("简体中文", zhItem.Text);
            Assert.Equal("English", enItem.Text);
            Assert.Equal("退出", exitItem.Text);

            Assert.True(zhItem.Checked);
            Assert.False(enItem.Checked);
        });
    }

    [Fact]
    public void SetLanguage_ZhCnToEnUs_RefreshesAllPermanentMenuItems_AndSwapsChecks()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(UiLanguage.ZhCn);

            SetLanguage(ctx, UiLanguage.EnUs);

            var statusItem = GetPrivateField<ToolStripMenuItem>(ctx, "_statusItem");
            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");
            var openItem = GetPrivateField<ToolStripMenuItem>(ctx, "_openOutputFolderItem");
            var languageItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageItem");
            var zhItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageZhCnItem");
            var enItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageEnUsItem");
            var exitItem = GetPrivateField<ToolStripMenuItem>(ctx, "_exitItem");

            Assert.Equal("Status: Idle", statusItem.Text);
            Assert.Equal("Stop recording", stopItem.Text);
            Assert.Equal("Open output folder", openItem.Text);
            Assert.Equal("Language / 语言", languageItem.Text);
            Assert.Equal("简体中文", zhItem.Text);
            Assert.Equal("English", enItem.Text);
            Assert.Equal("Exit", exitItem.Text);

            Assert.False(zhItem.Checked);
            Assert.True(enItem.Checked);
        });
    }

    [Fact]
    public void SetLanguage_SameLanguage_IsIdempotent()
    {
        RunOnSta(() =>
        {
            var audit = new CaptureAuditLogger();
            using var ctx = CreateContext(UiLanguage.ZhCn, audit);

            var before = audit.Events.Count(e => e.evt == "tray.language_changed");

            SetLanguage(ctx, UiLanguage.ZhCn);

            var after = audit.Events.Count(e => e.evt == "tray.language_changed");
            Assert.Equal(before, after);

            var zhItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageZhCnItem");
            var enItem = GetPrivateField<ToolStripMenuItem>(ctx, "_languageEnUsItem");
            Assert.True(zhItem.Checked);
            Assert.False(enItem.Checked);
        });
    }

    [Fact]
    public void SetLanguage_WithPendingConfirmation_RefreshesMenuWithoutResolvingQueue()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(UiLanguage.ZhCn);

            var approved = false;
            var summary = new
            {
                source = "region: test",
                source_type = "region",
                source_title = "test",
                audio = "No audio",
                duration = "30s",
                output = "out.mp4",
                nested_role = "none",
                recording_id = "rec_lang",
                confirmation_id = "conf_lang",
                timeout_seconds = 60,
                expires_at = "2026-01-01T00:00:00Z"
            };

            ctx.RequestConfirmation(summary, decision => { approved = decision.Approved; });
            Application.DoEvents();
            Thread.Sleep(50);
            Application.DoEvents();

            var approveItem = GetPrivateField<ToolStripMenuItem>(ctx, "_approveItem");
            Assert.Contains("确认录屏", approveItem.Text);

            SetLanguage(ctx, UiLanguage.EnUs);

            var queue = GetPrivateField<ConfirmationQueue>(ctx, "_confirmationQueue");
            Assert.Equal(1, queue.PendingCount);

            Assert.Contains("Confirm recording", approveItem.Text);
            Assert.False(approved);
        });
    }

    [Fact]
    public void SetLanguage_WithActiveRecording_RefreshesStopTextWithoutStopping()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(UiLanguage.ZhCn);

            var rec = new Recording
            {
                SourceType = "region",
                StartedAtUtc = DateTime.UtcNow,
                Config = new CaptureConfig
                {
                    SourceKind = "region",
                    Bounds = (100, 100, 800, 600),
                    OutputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"test-lang-{Guid.NewGuid():N}.mp4")
                }
            };

            ctx.SetRecording(rec);

            var activeBefore = GetPrivateField<System.Collections.Generic.Dictionary<string, Recording>>(ctx, "_activeRecordings").Count;
            Assert.Equal(1, activeBefore);

            var stopItem = GetPrivateField<ToolStripMenuItem>(ctx, "_stopItem");
            Assert.Equal("停止录制", stopItem.Text);

            SetLanguage(ctx, UiLanguage.EnUs);

            var activeAfter = GetPrivateField<System.Collections.Generic.Dictionary<string, Recording>>(ctx, "_activeRecordings").Count;
            Assert.Equal(1, activeAfter);
            Assert.Equal("Stop recording", stopItem.Text);
        });
    }

    [Fact]
    public void SetLanguage_NewRegionSelectionForm_UsesCurrentLanguage()
    {
        RunOnSta(() =>
        {
            using var ctx = CreateContext(UiLanguage.ZhCn);
            SetLanguage(ctx, UiLanguage.EnUs);

            var uiText = GetPrivateField<IUiTextProvider>(ctx, "_uiText");
            var auditEvents = new System.Collections.Generic.List<(string Name, System.Text.Json.JsonElement Payload)>();
            using var form = TrayContext.CreateRegionSelectionForm(null,
                e => auditEvents.Add((e.EventName, System.Text.Json.JsonSerializer.SerializeToElement(e.Payload))),
                uiText);

            var confirmButton = form.AcceptButton as Button;
            var cancelButton = form.CancelButton as Button;
            Assert.NotNull(confirmButton);
            Assert.NotNull(cancelButton);
            Assert.Equal("Confirm (Enter)", confirmButton!.Text);
            Assert.Equal("Cancel (Esc)", cancelButton!.Text);
        });
    }
}
