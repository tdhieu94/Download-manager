using System.Drawing.Drawing2D;

namespace DoMa.App.UI;

/// <summary>Giá trị ô tiến độ: phần trăm (-1 = không rõ) và màu theo trạng thái.</summary>
public readonly record struct ProgressValue(int Percent, Color Color);

public sealed class ProgressColumn : DataGridViewColumn
{
    public ProgressColumn() : base(new ProgressCell())
    {
    }

    private sealed class ProgressCell : DataGridViewTextBoxCell
    {
        protected override void Paint(
            Graphics graphics,
            Rectangle clipBounds,
            Rectangle cellBounds,
            int rowIndex,
            DataGridViewElementStates cellState,
            object? value,
            object? formattedValue,
            string? errorText,
            DataGridViewCellStyle cellStyle,
            DataGridViewAdvancedBorderStyle advancedBorderStyle,
            DataGridViewPaintParts paintParts)
        {
            base.Paint(graphics, clipBounds, cellBounds, rowIndex, cellState, null, null, errorText, cellStyle, advancedBorderStyle, paintParts & ~DataGridViewPaintParts.ContentForeground);
            var progress = value is ProgressValue p ? p : new ProgressValue(-1, Theme.Neutral);
            var scale = (DataGridView?.DeviceDpi ?? 96) / 96f;
            var pad = cellStyle.Padding;
            var label = progress.Percent < 0 ? "—" : $"{progress.Percent}%";
            var labelWidth = (int)(40 * scale);
            var barHeight = 6 * scale;
            var bar = new RectangleF(
                cellBounds.X + pad.Left,
                cellBounds.Y + (cellBounds.Height - barHeight) / 2f,
                cellBounds.Width - pad.Left - pad.Right - labelWidth,
                barHeight);
            if (bar.Width <= 4)
                return;

            var state = graphics.Save();
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            Theme.FillRound(graphics, Theme.Track, bar, barHeight / 2);
            if (progress.Percent > 0)
            {
                var fill = bar with { Width = Math.Max(barHeight, bar.Width * progress.Percent / 100f) };
                Theme.FillRound(graphics, progress.Color, fill, barHeight / 2);
            }
            graphics.Restore(state);

            TextRenderer.DrawText(
                graphics,
                label,
                Theme.Small,
                new Rectangle((int)bar.Right, cellBounds.Y, labelWidth, cellBounds.Height),
                Theme.TextSecondary,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
