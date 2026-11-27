using OBK.Core;
using Graphics = global::Android.Graphics;

namespace OBK.AndroidApp;

/// <summary>Renders the reader's avatar artwork without creating MAUI views in a background worker.</summary>
internal static class NotificationAvatar
{
    internal static Graphics.Bitmap Create(IReadOnlyList<Login> known, Login? child, bool dark)
    {
        const int size = 192;
        var bitmap = Graphics.Bitmap.CreateBitmap(size, size, Graphics.Bitmap.Config.Argb8888!)!;
        using var canvas = new Graphics.Canvas(bitmap);
        using var paint = new Graphics.Paint(Graphics.PaintFlags.AntiAlias);
        using var typeface = Graphics.Typeface.Create(Graphics.Typeface.Default, Graphics.TypefaceStyle.Bold);
        paint.SetTypeface(typeface);
        paint.TextAlign = Graphics.Paint.Align.Center;
        if (child is not null)
            Draw(child, 0, 0, 1, AvatarPresentation.UseLargeInitial(child, known) ? 0.5 : 1d / 3);
        else
            foreach (var (account, position) in known.Take(3).Zip(ReaderTheme.GroupAvatarPositions(known.Count)))
                Draw(account, position.X, position.Y, position.Size, 0.5);
        return bitmap;

        void Draw(Login account, double x, double y, double scale, double fontScale)
        {
            var colors = ReaderTheme.AvatarColors[AvatarPresentation.PaletteIndex(account.Url + "|" + account.Username)];
            var diameter = (float)(size * scale);
            var centerX = (float)(size * x) + diameter / 2;
            var centerY = (float)(size * y) + diameter / 2;
            paint.Color = Graphics.Color.ParseColor(dark ? colors.DarkBackground : colors.LightBackground);
            canvas.DrawCircle(centerX, centerY, diameter / 2, paint);
            paint.Color = Graphics.Color.ParseColor(dark ? colors.DarkText : colors.LightText);
            paint.TextSize = (float)(diameter * fontScale);
            canvas.DrawText(AvatarPresentation.Initials(ReaderPage.AccountLabelConverter.Label(account)), centerX,
                centerY - (paint.Ascent() + paint.Descent()) / 2, paint);
        }
    }
}
