using System;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using AgentRecorder.App;
using AgentRecorder.Windows;
using Xunit;

namespace AgentRecorder.Tests;

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
}
