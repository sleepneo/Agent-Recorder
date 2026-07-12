using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Infrastructure;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

[Collection("NonParallel-SystemQueryProviders")]
public class RegionSelectionFormTests
{
    private static T GetPrivateField<T>(object obj, string fieldName)
    {
        var field = obj.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        return (T)field!.GetValue(obj)!;
    }

    private static T RunOnSta<T>(Func<T> func)
    {
        T result = default!;
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = func();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
        return result;
    }

    private static void RunOnSta(Action action)
    {
        Exception? ex = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                ex = e;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (ex != null)
            throw new System.Reflection.TargetInvocationException(ex);
    }

    [Fact]
    public void InitialBounds_InsideClientArea_EnablesConfirmButton()
    {
        RunOnSta(() =>
        {
            // Virtual screen starts at (0,0); form bounds match virtual screen.
            using var form = new RegionSelectionForm(new Rectangle(100, 150, 800, 600));
            form.Show();
            form.Shown += (_, _) => { };

            // Trigger OnShown logic explicitly to ensure ApplyInitialSelection runs.
            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void InitialBounds_OutsideClientArea_Ignored()
    {
        RunOnSta(() =>
        {
            // Bounds far outside the form's client area should be ignored.
            using var form = new RegionSelectionForm(new Rectangle(10000, 10000, 800, 600));
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void InitialBounds_TooSmall_Ignored()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm(new Rectangle(100, 100, 10, 10));
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            form.Close();
        });
    }

    // -------------------------------------------------------------------------
    // Bilingual UI and DPI-safe layout tests
    // -------------------------------------------------------------------------

    [Theory]
    [InlineData(UiLanguage.ZhCn, "RegionSelection_Button_Confirm", "确认 (Enter)")]
    [InlineData(UiLanguage.EnUs, "RegionSelection_Button_Confirm", "Confirm (Enter)")]
    [InlineData(UiLanguage.ZhCn, "RegionSelection_Button_Cancel", "取消 (Esc)")]
    [InlineData(UiLanguage.EnUs, "RegionSelection_Button_Cancel", "Cancel (Esc)")]
    public void Constructor_Localized_ButtonTextMatchesProvider(UiLanguage language, string key, string expected)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var form = new RegionSelectionForm(textProvider: text);
            var button = key switch
            {
                "RegionSelection_Button_Confirm" => form.AcceptButton as Button,
                "RegionSelection_Button_Cancel" => form.CancelButton as Button,
                _ => null
            };
            Assert.NotNull(button);
            Assert.Equal(expected, button!.Text);
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Constructor_Localized_ButtonWidthFitsText(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var form = new RegionSelectionForm(textProvider: text);
            form.Show();

            var confirmButton = form.AcceptButton as Button;
            var cancelButton = form.CancelButton as Button;
            Assert.NotNull(confirmButton);
            Assert.NotNull(cancelButton);

            // The button must be at least as wide as the rendered text plus a small padding allowance.
            var confirmMeasured = TextRenderer.MeasureText(confirmButton!.Text, confirmButton.Font).Width;
            var cancelMeasured = TextRenderer.MeasureText(cancelButton!.Text, cancelButton.Font).Width;
            Assert.True(confirmButton.Width >= confirmMeasured + 20,
                $"Confirm button width {confirmButton.Width} too small for text '{confirmButton.Text}' (measured {confirmMeasured})");
            Assert.True(cancelButton.Width >= cancelMeasured + 20,
                $"Cancel button width {cancelButton.Width} too small for text '{cancelButton.Text}' (measured {cancelMeasured})");

            form.Close();
        });
    }

    [Fact]
    public void Constructor_Bilingual_LongestTextStillFitsButton()
    {
        RunOnSta(() =>
        {
            using var zhForm = new RegionSelectionForm(textProvider: new UiTextProvider(UiLanguage.ZhCn));
            using var enForm = new RegionSelectionForm(textProvider: new UiTextProvider(UiLanguage.EnUs));
            zhForm.Show();
            enForm.Show();

            var zhConfirm = zhForm.AcceptButton as Button;
            var enConfirm = enForm.AcceptButton as Button;
            Assert.NotNull(zhConfirm);
            Assert.NotNull(enConfirm);

            var zhMeasured = TextRenderer.MeasureText(zhConfirm!.Text, zhConfirm.Font).Width;
            var enMeasured = TextRenderer.MeasureText(enConfirm!.Text, enConfirm.Font).Width;

            // Both languages' buttons must be wide enough for their own longest text.
            Assert.True(zhConfirm.Width >= zhMeasured + 20);
            Assert.True(enConfirm.Width >= enMeasured + 20);

            zhForm.Close();
            enForm.Close();
        });
    }

    [Fact]
    public void Constructor_ButtonsDoNotOverlapEachOther()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.EnUs);
            using var form = new RegionSelectionForm(textProvider: text);
            form.Show();

            var confirm = form.AcceptButton as Button;
            var cancel = form.CancelButton as Button;
            Assert.NotNull(confirm);
            Assert.NotNull(cancel);

            // Bounds are relative to the form (both buttons are direct children of the form).
            Assert.False(confirm!.Bounds.IntersectsWith(cancel!.Bounds),
                "Confirm and cancel buttons must not overlap");

            form.Close();
        });
    }

    [Theory]
    [InlineData(UiLanguage.ZhCn)]
    [InlineData(UiLanguage.EnUs)]
    public void Constructor_ControlsAreInsideClientAreaAndDoNotOverlap(UiLanguage language)
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(language);
            using var form = new RegionSelectionForm(textProvider: text);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            var confirm = form.AcceptButton as Button;
            var cancel = form.CancelButton as Button;
            var infoLabel = GetPrivateField<Label>(form, "_infoLabel");
            var coordsLabel = GetPrivateField<Label>(form, "_coordsLabel");
            var displayLabel = GetPrivateField<Label>(form, "_displayLabel");
            var controlPanel = GetPrivateField<Panel>(form, "_controlPanel");

            Assert.NotNull(confirm);
            Assert.NotNull(cancel);
            Assert.True(client.Contains(confirm!.Bounds));
            Assert.True(client.Contains(cancel!.Bounds));
            Assert.True(client.Contains(infoLabel.Bounds));
            Assert.True(client.Contains(coordsLabel.Bounds));
            Assert.True(client.Contains(displayLabel.Bounds));
            Assert.True(client.Contains(controlPanel.Bounds));

            Assert.False(confirm.Bounds.IntersectsWith(cancel.Bounds));
            Assert.False(controlPanel.Bounds.IntersectsWith(confirm.Bounds));
            Assert.False(controlPanel.Bounds.IntersectsWith(cancel.Bounds));

            form.Close();
        });
    }

    [Fact]
    public void Constructor_ButtonsAreInsideClientArea()
    {
        RunOnSta(() =>
        {
            var text = new UiTextProvider(UiLanguage.ZhCn);
            using var form = new RegionSelectionForm(textProvider: text);
            form.Show();

            var client = new Rectangle(0, 0, form.ClientSize.Width, form.ClientSize.Height);
            var confirm = form.AcceptButton as Button;
            var cancel = form.CancelButton as Button;
            Assert.NotNull(confirm);
            Assert.NotNull(cancel);
            Assert.True(client.Contains(confirm!.Bounds));
            Assert.True(client.Contains(cancel!.Bounds));

            form.Close();
        });
    }

    [Fact]
    public void InitialBounds_SyncsInputFieldsAndDisplayLabel()
    {
        RunOnSta(() =>
        {
            var initial = new Rectangle(120, 80, 640, 480);
            using var form = new RegionSelectionForm(initial);
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);

            var inputX = GetPrivateField<NumericUpDown>(form, "_inputX");
            var inputY = GetPrivateField<NumericUpDown>(form, "_inputY");
            var inputW = GetPrivateField<NumericUpDown>(form, "_inputW");
            var inputH = GetPrivateField<NumericUpDown>(form, "_inputH");
            var displayLabel = GetPrivateField<Label>(form, "_displayLabel");

            Assert.Equal(form.Bounds.X + initial.X, (int)inputX.Value);
            Assert.Equal(form.Bounds.Y + initial.Y, (int)inputY.Value);
            Assert.Equal(initial.Width, (int)inputW.Value);
            Assert.Equal(initial.Height, (int)inputH.Value);

            var virtualBounds = new Rectangle(
                form.Bounds.X + initial.X,
                form.Bounds.Y + initial.Y,
                initial.Width,
                initial.Height);

            string expectedId;
            try
            {
                var displays = SystemQuery.EnumDisplays();
                expectedId = RegionSelectionGeometry.FindDisplayId(virtualBounds, displays)
                          ?? RegionSelectionGeometry.FindDisplayIdByOverlap(virtualBounds, displays)
                          ?? "unknown";
            }
            catch
            {
                expectedId = "unknown";
            }

            Assert.Contains(expectedId, displayLabel.Text);

            form.Close();
        });
    }

    [Fact]
    public void NumericInput_ChangesDimensions_UpdatesSelection()
    {
        RunOnSta(() =>
        {
            var initial = new Rectangle(100, 150, 800, 600);
            using var form = new RegionSelectionForm(initial);
            form.Show();

            var inputW = GetPrivateField<NumericUpDown>(form, "_inputW");
            var inputH = GetPrivateField<NumericUpDown>(form, "_inputH");

            inputW.Value = 400;
            inputH.Value = 300;

            var selection = GetPrivateField<Rectangle>(form, "_selection");
            var acceptButton = form.AcceptButton as Button;

            Assert.Equal(400, selection.Width);
            Assert.Equal(300, selection.Height);
            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void NumericInput_ChangesPosition_UpdatesSelection()
    {
        RunOnSta(() =>
        {
            var initial = new Rectangle(100, 150, 800, 600);
            using var form = new RegionSelectionForm(initial);
            form.Show();

            var inputX = GetPrivateField<NumericUpDown>(form, "_inputX");
            var inputY = GetPrivateField<NumericUpDown>(form, "_inputY");

            inputX.Value = form.Bounds.X + 50;
            inputY.Value = form.Bounds.Y + 60;

            var selection = GetPrivateField<Rectangle>(form, "_selection");

            Assert.Equal(50, selection.X);
            Assert.Equal(60, selection.Y);

            form.Close();
        });
    }

    [Fact]
    public void NumericInput_OddDimensions_AreNormalizedToEven()
    {
        RunOnSta(() =>
        {
            var initial = new Rectangle(100, 150, 800, 600);
            using var form = new RegionSelectionForm(initial);
            form.Show();

            var inputW = GetPrivateField<NumericUpDown>(form, "_inputW");
            inputW.Value = 401;

            var selection = GetPrivateField<Rectangle>(form, "_selection");

            Assert.Equal(400, selection.Width);

            form.Close();
        });
    }

    [Fact]
    public void PresetButton_1280x720_FromExistingSelection_CentersAndNormalizes()
    {
        RunOnSta(() =>
        {
            var initial = new Rectangle(300, 200, 500, 400);
            using var form = new RegionSelectionForm(initial);
            form.Show();

            var preset720 = GetPrivateField<Button>(form, "_preset720");
            preset720.PerformClick();

            var selection = GetPrivateField<Rectangle>(form, "_selection");
            var acceptButton = form.AcceptButton as Button;

            var centerVirtual = new Point(
                form.Bounds.X + initial.X + initial.Width / 2,
                form.Bounds.Y + initial.Y + initial.Height / 2);

            var expected = RegionSelectionGeometry.ApplyPresetSizeAroundCenter(
                form.Bounds, centerVirtual, new Size(1280, 720), 32);

            Assert.NotNull(expected);
            Assert.Equal(expected.Value, selection);
            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);

            form.Close();
        });
    }

    [Fact]
    public void PresetButton_1280x720_NearRightEdge_PreservesWidth()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm();

            // Place a small selection near the right edge so centering 1280x720
            // would overflow, but the screen should still fit the preset size.
            int virtualX = form.Bounds.X + form.Bounds.Width - 300;
            int virtualY = form.Bounds.Y + 200;
            form.SetInitialVirtualBounds(new Rectangle(virtualX, virtualY, 200, 200));

            form.Show();

            var preset720 = GetPrivateField<Button>(form, "_preset720");
            preset720.PerformClick();

            var selection = GetPrivateField<Rectangle>(form, "_selection");
            var acceptButton = form.AcceptButton as Button;

            if (form.Bounds.Width >= 1280 && form.Bounds.Height >= 720)
            {
                Assert.Equal(1280, selection.Width);
                Assert.Equal(720, selection.Height);
            }

            Assert.NotNull(acceptButton);
            Assert.True(acceptButton!.Enabled);
            Assert.True(selection.Width <= form.Bounds.Width);
            Assert.True(selection.Height <= form.Bounds.Height);

            form.Close();
        });
    }

    [Fact]
    public void PresetButton_Fit16x9_NoSelection_ProducesValidSelection()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm();
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            var presetFit = GetPrivateField<Button>(form, "_presetFit16x9");
            presetFit.PerformClick();

            var selection = GetPrivateField<Rectangle>(form, "_selection");

            Assert.True(selection.Width >= 32);
            Assert.True(selection.Height >= 32);
            Assert.True(selection.Width <= form.Bounds.Width);
            Assert.True(selection.Height <= form.Bounds.Height);
            Assert.True(selection.Width % 2 == 0);
            Assert.True(selection.Height % 2 == 0);
            Assert.True(acceptButton.Enabled);

            double ratio = (double)selection.Width / selection.Height;
            Assert.InRange(ratio, 16.0 / 9.0 - 0.05, 16.0 / 9.0 + 0.05);

            form.Close();
        });
    }

    // -------------------------------------------------------------------------
    // Window pick and snapping tests
    // -------------------------------------------------------------------------

    [Fact]
    public void InjectedWindow_AppearsAsCandidateAndSnapTarget()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Single(form.WindowCandidates);
                Assert.Contains(new Rectangle(120, 80, 640, 480), form.SnapTargets);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void MinimizedWindow_IsNotCandidate()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, true,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Empty(form.WindowCandidates);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void EmptyTitleWindow_IsNotCandidate()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "", "", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Empty(form.WindowCandidates);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void ApplyWindowPick_SetsSelectionAndEnablesConfirm()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm();
            form.Show();

            var acceptButton = form.AcceptButton as Button;
            Assert.NotNull(acceptButton);
            Assert.False(acceptButton!.Enabled);

            form.ApplyWindowPickForTest(new Rectangle(120, 80, 640, 480));

            Assert.True(acceptButton.Enabled);
            Assert.Equal(new Rectangle(120, 80, 640, 480), form.CurrentSelection);

            form.Close();
        });
    }

    [Fact]
    public void HoverWindow_AtWindowPoint_SetsHighlightBounds()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                // Set the mouse-down origin at the hover point so the move is not
                // interpreted as a drag-to-create gesture.
                var onMouseDown = form.GetType().GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var onMouseMove = form.GetType().GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseDown);
                Assert.NotNull(onMouseMove);
                onMouseDown!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 200, 200, 0) });
                onMouseMove!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.None, 0, 200, 200, 0) });

                Assert.Equal(new Rectangle(120, 80, 640, 480), form.HoverWindowClientBounds);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void HoverWindow_OverResizeHandle_PriorityGoesToSelection()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 100, virtualScreen.Y + 100, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.ApplyWindowPickForTest(new Rectangle(120, 120, 400, 300));
                form.RefreshCandidatesAndTargetsForTest();

                // Mouse over the selection's SE resize handle (inside the injected window).
                var onMouseMove = form.GetType().GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseMove);
                onMouseMove!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.None, 0, 520, 420, 0) });

                Assert.Null(form.HoverWindowClientBounds);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void MouseMoveWithoutPriorMouseDown_DoesNotStartCreateSelection()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 100, virtualScreen.Y + 100, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                var onMouseMove = form.GetType().GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseMove);

                // Move the mouse far from the default (0,0) origin without any MouseDown.
                onMouseMove!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.None, 0, 500, 500, 0) });

                Assert.Equal("None", form.CurrentDragModeForTests);
                Assert.Equal(Rectangle.Empty, form.CurrentSelection);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void HoverWithoutMouseDown_CanHighlightWindowButDoesNotCreateSelection()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                var onMouseMove = form.GetType().GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseMove);
                onMouseMove!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.None, 0, 200, 200, 0) });

                Assert.Equal(new Rectangle(120, 80, 640, 480), form.HoverWindowClientBounds);
                Assert.Equal(Rectangle.Empty, form.CurrentSelection);
                Assert.Equal("None", form.CurrentDragModeForTests);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void ExistingSelection_DragOutsideSelection_StartsNewSelection()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>());

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.ApplyWindowPickForTest(new Rectangle(120, 120, 400, 300));
                form.RefreshCandidatesAndTargetsForTest();

                var onMouseDown = form.GetType().GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var onMouseMove = form.GetType().GetMethod("OnMouseMove", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseDown);
                Assert.NotNull(onMouseMove);

                // Press in blank area outside the existing selection.
                onMouseDown!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 50, 50, 0) });
                // Drag beyond click tolerance.
                onMouseMove!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 100, 100, 0) });

                Assert.Equal("Create", form.CurrentDragModeForTests);
                Assert.Equal(new Rectangle(50, 50, 50, 50), form.CurrentSelection);
                var acceptButton = form.AcceptButton as Button;
                Assert.NotNull(acceptButton);
                Assert.False(acceptButton!.Enabled);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void ExistingSelection_ClickOutsideSelection_DoesNotClearSelection()
    {
        RunOnSta(() =>
        {
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>());

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.ApplyWindowPickForTest(new Rectangle(120, 120, 400, 300));
                form.RefreshCandidatesAndTargetsForTest();

                var onMouseDown = form.GetType().GetMethod("OnMouseDown", BindingFlags.NonPublic | BindingFlags.Instance);
                var onMouseUp = form.GetType().GetMethod("OnMouseUp", BindingFlags.NonPublic | BindingFlags.Instance);
                Assert.NotNull(onMouseDown);
                Assert.NotNull(onMouseUp);

                // Press and release in blank area without dragging beyond tolerance.
                onMouseDown!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 50, 50, 0) });
                onMouseUp!.Invoke(form, new object[] { new MouseEventArgs(MouseButtons.Left, 0, 51, 51, 0) });

                Assert.Equal(new Rectangle(120, 120, 400, 300), form.CurrentSelection);
                var acceptButton = form.AcceptButton as Button;
                Assert.NotNull(acceptButton);
                Assert.True(acceptButton!.Enabled);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void TinyOrFullScreenOverlayWindow_IsNotPickCandidate()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_tiny", "Tiny", "tiny.exe", 1, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 10, virtualScreen.Y + 10, 10, 10)),
                new SystemQuery.WindowInfo("window_overlay", "Overlay", "overlay.exe", 2, false, false,
                    new SystemQuery.Bounds(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Empty(form.WindowCandidateBoundsForTests);
                Assert.DoesNotContain(new Rectangle(10, 10, 10, 10), form.SnapTargets);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void PartiallyOffscreenWindowPick_IsClampedToClientArea()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            // Window extends beyond the right/bottom edges of the virtual screen.
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_offscreen", "Offscreen", "offscreen.exe", 1, false, false,
                    new SystemQuery.Bounds(
                        virtualScreen.X + virtualScreen.Width - 200,
                        virtualScreen.Y + virtualScreen.Height - 200,
                        1000,
                        1000))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                var candidates = form.WindowCandidateBoundsForTests;
                Assert.Single(candidates);
                var pickBounds = candidates[0];
                Assert.True(pickBounds.X >= 0);
                Assert.True(pickBounds.Y >= 0);
                Assert.True(pickBounds.Right <= form.Bounds.Width);
                Assert.True(pickBounds.Bottom <= form.Bounds.Height);
                Assert.True(pickBounds.Width >= 32);
                Assert.True(pickBounds.Height >= 32);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void WindowCandidates_AndSnapTargets_UseConsistentFilteredBounds()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_valid", "Valid", "valid.exe", 1, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480)),
                new SystemQuery.WindowInfo("window_tiny", "Tiny", "tiny.exe", 2, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 10, virtualScreen.Y + 10, 10, 10)),
                new SystemQuery.WindowInfo("window_minimized", "Minimized", "min.exe", 3, false, true,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                var candidateBounds = form.WindowCandidateBoundsForTests;
                Assert.Single(candidateBounds);
                Assert.Equal(new Rectangle(120, 80, 640, 480), candidateBounds[0]);

                // Display targets are also present, so we only assert the valid window target exists.
                Assert.Contains(new Rectangle(120, 80, 640, 480), form.SnapTargets);
                Assert.DoesNotContain(new Rectangle(10, 10, 10, 10), form.SnapTargets);
            }
            finally
            {
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void RefreshCandidatesAndTargets_WhenWindowProviderThrows_KeepsDisplayTargets()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetDisplayProvider(() => new List<SystemQuery.DisplayInfo>
            {
                new SystemQuery.DisplayInfo("display_1", "Display 1", true,
                    new SystemQuery.Bounds(virtualScreen.X, virtualScreen.Y, virtualScreen.Width, virtualScreen.Height), 1.0)
            });
            SystemQuery.SetWindowProvider((_, _) => throw new InvalidOperationException("EnumWindows failure"));

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Empty(form.WindowCandidates);
                Assert.Contains(new Rectangle(0, 0, form.Bounds.Width, form.Bounds.Height), form.SnapTargets);
            }
            finally
            {
                SystemQuery.SetDisplayProvider(null);
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    [Fact]
    public void RefreshCandidatesAndTargets_WhenDisplayProviderThrows_KeepsWindowTargets()
    {
        RunOnSta(() =>
        {
            var virtualScreen = SystemInformation.VirtualScreen;
            SystemQuery.SetDisplayProvider(() => throw new InvalidOperationException("EnumDisplays failure"));
            SystemQuery.SetWindowProvider((_, _) => new List<SystemQuery.WindowInfo>
            {
                new SystemQuery.WindowInfo("window_1", "Notepad", "notepad.exe", 123, false, false,
                    new SystemQuery.Bounds(virtualScreen.X + 120, virtualScreen.Y + 80, 640, 480))
            });

            try
            {
                using var form = new RegionSelectionForm();
                form.Show();
                form.RefreshCandidatesAndTargetsForTest();

                Assert.Single(form.WindowCandidateBoundsForTests);
                Assert.Contains(new Rectangle(120, 80, 640, 480), form.SnapTargets);
            }
            finally
            {
                SystemQuery.SetDisplayProvider(null);
                SystemQuery.SetWindowProvider(null);
            }
        });
    }

    // -------------------------------------------------------------------------
    // Top-most / foregrounding and audit tests (Task 147 + Task 148)
    // -------------------------------------------------------------------------

    [Fact]
    public void FormStyle_IsBorderlessTopMostNoTaskbarAndCoversVirtualScreen()
    {
        RunOnSta(() =>
        {
            using var form = new RegionSelectionForm();
            Assert.Equal(FormBorderStyle.None, form.FormBorderStyle);
            Assert.False(form.ShowInTaskbar);
            Assert.True(form.TopMost);
            Assert.False(form.MaximizeBox);
            Assert.False(form.MinimizeBox);
            Assert.Equal(SystemInformation.VirtualScreen, form.Bounds);
        });
    }

    [Fact]
    public void Constructor_RaisesUiCreatedAuditEvent()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake,
                onAuditEvent: e => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload))));

            var created = auditEvents.FirstOrDefault(e => e.Name == "region_selection.ui_created");
            Assert.Equal("region_selection.ui_created", created.Name);
            Assert.Equal("handle_created", created.Payload.GetProperty("stage").GetString());
            Assert.True(created.Payload.GetProperty("topmost").GetBoolean());
        });
    }

    [Fact]
    public void OnShown_PerformsTopMostForegroundAndAuditsResult()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            // Force handle creation so the fake can report the form as foreground.
            fake.ForegroundWindow = form.Handle;

            form.Show();
            Application.DoEvents();

            Assert.Single(fake.TopMostCalls);
            Assert.Single(fake.ForegroundCalls);
            Assert.Empty(fake.BringToTopCalls);

            Assert.Contains(auditEvents, e => e.Name == "region_selection.ui_shown");
            Assert.Contains(auditEvents, e => e.Name == "region_selection.foreground_attempt");
            Assert.Contains(auditEvents, e => e.Name == "region_selection.foreground_result");

            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.Equal(1, result.GetProperty("attempt").GetInt32());
            Assert.True(result.GetProperty("became_foreground").GetBoolean());
            Assert.True(result.GetProperty("set_window_pos_success").GetBoolean());
            Assert.True(result.GetProperty("set_foreground_window_success").GetBoolean());

            form.Close();
        });
    }

    [Fact]
    public void OnShown_SetForegroundFails_FallsBackToBringToTop()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator { SetForegroundResult = false, BringToTopResult = true };
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();

            Assert.Single(fake.BringToTopCalls);

            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.False(result.GetProperty("set_foreground_window_success").GetBoolean());
            Assert.True(result.GetProperty("bring_window_to_top_success").GetBoolean());
            Assert.True(result.GetProperty("became_foreground").GetBoolean());

            form.Close();
        });
    }

    [Fact]
    public void OnShown_NeverBecomesForeground_RetriesOnlyUpToMaxAttempts()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator
            {
                SetForegroundResult = false,
                BringToTopResult = false,
                ForegroundWindow = (IntPtr)0x1234
            };
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            form.Show();
            Application.DoEvents();

            // Immediate attempt only so far.
            Assert.Single(fake.TopMostCalls);
            Assert.Single(fake.ForegroundCalls);
            Assert.Single(fake.BringToTopCalls);

            // Simulate the delayed verification tick.
            form.RunForegroundVerificationForTest();

            Assert.Equal(2, fake.TopMostCalls.Count);
            Assert.Equal(2, fake.ForegroundCalls.Count);
            Assert.Equal(2, fake.BringToTopCalls.Count);
            Assert.Equal(2, form.ForegroundAttemptsForTest);

            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.False(result.GetProperty("became_foreground").GetBoolean());

            // Further manual ticks are no-ops because the limit has been reached.
            form.RunForegroundVerificationForTest();
            Assert.Equal(2, fake.TopMostCalls.Count);

            form.Close();
            Application.DoEvents();
        });
    }

    [Fact]
    public void OnShown_ActivatorThrows_ExceptionCapturedAndDoesNotPropagate()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator { ThrowOnSetTopMost = true, ThrowMessage = "settopmost failure" };
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            fake.ForegroundWindow = (IntPtr)0x1234;
            form.Show();
            Application.DoEvents();

            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.False(result.GetProperty("became_foreground").GetBoolean());
            Assert.Contains("settopmost failure", result.GetProperty("error").GetString());
            Assert.Equal("set_topmost", result.GetProperty("error_stage").GetString());

            form.Close();
        });
    }

    // -------------------------------------------------------------------------
    // Task 148 additions
    // -------------------------------------------------------------------------

    [Fact]
    public void OnShown_DelayedVerification_RunsEvenAfterImmediateSuccess()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();

            // Immediate attempt succeeded.
            Assert.Single(fake.TopMostCalls);
            var firstResult = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.True(firstResult.GetProperty("became_foreground").GetBoolean());

            // Simulate the one-time delayed verification tick.
            form.RunForegroundVerificationForTest();

            Assert.Equal(2, fake.TopMostCalls.Count);
            Assert.Equal(2, form.ForegroundAttemptsForTest);
            var secondResult = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.Equal(2, secondResult.GetProperty("attempt").GetInt32());
            Assert.True(secondResult.GetProperty("became_foreground").GetBoolean());

            form.Close();
        });
    }

    [Fact]
    public void OnShown_DelayedVerification_ReclaimsForegroundWhenStolen()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();

            // Another window steals the foreground before the delayed tick.
            fake.ForegroundWindow = (IntPtr)0xBEEF;
            form.RunForegroundVerificationForTest();

            Assert.Equal(2, fake.TopMostCalls.Count);
            Assert.Equal(2, fake.ForegroundCalls.Count);
            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.Equal(2, result.GetProperty("attempt").GetInt32());
            Assert.False(result.GetProperty("became_foreground").GetBoolean());

            form.Close();
        });
    }

    [Fact]
    public void OnShown_DelayedVerification_StopsAfterMaxAttempts()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();
            form.RunForegroundVerificationForTest();

            Assert.Equal(2, fake.TopMostCalls.Count);
            Assert.Equal(2, form.ForegroundAttemptsForTest);

            // Third trigger must be ignored.
            form.RunForegroundVerificationForTest();
            Assert.Equal(2, fake.TopMostCalls.Count);
            Assert.Equal(2, form.ForegroundAttemptsForTest);

            form.Close();
        });
    }

    [Fact]
    public void OnShown_ClosedBeforeDelayedVerification_NoFurtherActivatorCalls()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();
            Assert.Single(fake.TopMostCalls);

            form.Close();
            Application.DoEvents();

            // Simulate a delayed tick arriving after the form has been closed.
            form.RunForegroundVerificationForTest();
            Assert.Single(fake.TopMostCalls);

            form.Dispose();
        });
    }

    [Fact]
    public void ProductionAuditWiring_CallbackReceivesAllLifecycleEventsOnce()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator();
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake,
                onAuditEvent: e => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload))))
            {
                EnableDelayedForegroundVerification = false
            };

            fake.ForegroundWindow = form.Handle;
            form.Show();
            Application.DoEvents();

            Assert.Contains(auditEvents, e => e.Name == "region_selection.ui_created");
            Assert.Contains(auditEvents, e => e.Name == "region_selection.ui_shown");
            Assert.Contains(auditEvents, e => e.Name == "region_selection.foreground_attempt");
            Assert.Contains(auditEvents, e => e.Name == "region_selection.foreground_result");

            Assert.Single(auditEvents, e => e.Name == "region_selection.ui_created");
            Assert.Single(auditEvents, e => e.Name == "region_selection.ui_shown");

            form.Close();
        });
    }

    [Fact]
    public void TrayContextFactory_ForwardsUiCreatedToAuditCallback()
    {
        RunOnSta(() =>
        {
            var auditEvents = new List<(string Name, JsonElement Payload)>();
            using var form = TrayContext.CreateRegionSelectionForm(null,
                e => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload))),
                new UiTextProvider(UiLanguageStore.LoadOrDefault()));

            Assert.Single(auditEvents, e => e.Name == "region_selection.ui_created");
            Assert.Equal("handle_created", auditEvents[0].Payload.GetProperty("stage").GetString());
        });
    }

    [Fact]
    public void OnShown_GetForegroundWindowThrows_ExceptionCapturedAndDoesNotPropagate()
    {
        RunOnSta(() =>
        {
            var fake = new FakeWindowActivator
            {
                ThrowOnGetForegroundWindow = true,
                GetForegroundWindowExceptionMessage = "fg-read failed"
            };
            var auditEvents = new List<(string Name, JsonElement Payload)>();

            using var form = new RegionSelectionForm(null, fake)
            {
                EnableDelayedForegroundVerification = false
            };
            form.AuditEvent += (_, e) => auditEvents.Add((e.EventName, JsonSerializer.SerializeToElement(e.Payload)));

            form.Show();
            Application.DoEvents();

            var result = auditEvents.Last(e => e.Name == "region_selection.foreground_result").Payload;
            Assert.False(result.GetProperty("became_foreground").GetBoolean());
            Assert.Contains("fg-read failed", result.GetProperty("error").GetString());
            Assert.Equal("get_foreground_before", result.GetProperty("error_stage").GetString());

            form.Close();
        });
    }

    private sealed class FakeWindowActivator : IWindowActivator
    {
        public List<IntPtr> TopMostCalls { get; } = new();
        public List<IntPtr> ForegroundCalls { get; } = new();
        public List<IntPtr> BringToTopCalls { get; } = new();
        public int GetForegroundWindowCallCount { get; private set; }

        public bool SetTopMostResult { get; set; } = true;
        public bool SetForegroundResult { get; set; } = true;
        public bool BringToTopResult { get; set; } = true;
        public bool ThrowOnSetTopMost { get; set; }
        public bool ThrowOnGetForegroundWindow { get; set; }
        public string? ThrowMessage { get; set; }
        public string? GetForegroundWindowExceptionMessage { get; set; }
        public IntPtr ForegroundWindow { get; set; } = IntPtr.Zero;

        public bool SetTopMost(IntPtr hWnd)
        {
            TopMostCalls.Add(hWnd);
            if (ThrowOnSetTopMost)
                throw new InvalidOperationException(ThrowMessage ?? "SetTopMost failed");
            return SetTopMostResult;
        }

        public bool SetForeground(IntPtr hWnd)
        {
            ForegroundCalls.Add(hWnd);
            return SetForegroundResult;
        }

        public bool BringToTop(IntPtr hWnd)
        {
            BringToTopCalls.Add(hWnd);
            return BringToTopResult;
        }

        public IntPtr GetForegroundWindow()
        {
            GetForegroundWindowCallCount++;
            if (ThrowOnGetForegroundWindow)
                throw new InvalidOperationException(GetForegroundWindowExceptionMessage ?? "GetForegroundWindow failed");
            return ForegroundWindow;
        }
    }
}
