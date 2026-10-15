using System;
using UnityEngine;

namespace Apocasetter
{
    internal static class SteelCable
    {
        internal static Color[] Pixels(int width, int height)
        {
            var pixels = new Color[width * height];
            double center = (height - 1) / 2.0, radius = height * 0.27;
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    double dy = y - center;
                    double edge = radius + 0.6 - Math.Abs(dy);
                    int strand = (int)Math.Floor((dy + radius) / 2.1);
                    double left = 10 + (strand * 7 % 6), right = width - 11 - (strand * 5 % 7);
                    double alpha = Math.Max(0, Math.Min(1, edge)) * Math.Max(0, Math.Min(1, Math.Min(x - left, right - x) + 1));
                    double twist = Math.Sin(x * 0.34 + Math.Asin(Math.Max(-1, Math.Min(1, dy / radius))) * 4.3);
                    double wires = Math.Sin(x * 0.95 + dy * 2.8);
                    double light = 0.34 + 0.30 * Math.Sqrt(Math.Max(0, 1 - dy * dy / (radius * radius))) + 0.17 * twist + 0.07 * wires;
                    // A few separated wire tips extend past the uneven cut on either end.
                    if (x < 18 || x > width - 19)
                        for (int wire = -2; wire <= 2; wire++)
                        {
                            double endX = x < 18 ? x : width - 1 - x;
                            double wireY = center + wire * 2.2 + (12 - endX) * wire * 0.11;
                            if (endX >= 2 + (wire + 2) * 1.3 && endX < 17)
                                alpha = Math.Max(alpha, Math.Max(0, 1 - Math.Abs(y - wireY)) * 0.95);
                        }
                    light = Math.Max(0.12, Math.Min(0.9, light));
                    pixels[y * width + x] = new Color((float)(light * 0.98), (float)(light * 0.93), (float)(light * 0.82), (float)alpha);
                }
            return pixels;
        }

    }
}
