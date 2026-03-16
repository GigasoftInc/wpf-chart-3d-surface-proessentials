using System;
using System.Diagnostics;
using System.IO;
using System.Drawing;
using System.Windows;
using System.Collections.Generic;

namespace GigaPrime3D
{


    public enum LengthUnit
    {
        None,
        NM, // Nanometers
        UM, // Microns
        MM, // Millimeters
        CM, // Centimeters
        MT, // Meters
        UI, // Micro-Inches
        TH, // Thou
        IN, // Inches
        FT, // Feet 
    }

    // Class to encapsulate a heightmap file and represent its size, data, possible units //

    public class HeightMap
    {
        public HeightMap(string path)
        {
            IsValid = true;
            Path = path;

            int nLen = path.Length;
            string fExt = path.Substring(nLen - 3, 3);

            if (fExt == "bhm")
            {
                // Header is 32 bytes long

                FileStream file = null;

                try
                {
                    file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                }
                catch
                {
                    IsValid = false;
                    Path = "";
                    MessageBox.Show("Unable to find demo file.  File does not exist.");
                    System.Windows.Application.Current.Shutdown();
                }

                //using var reader = new BinaryReader(file);
                var reader = new BinaryReader(file);

                WidthPx = reader.ReadInt32();
                HeightPx = reader.ReadInt32();
                MinZMm = reader.ReadSingle();
                MaxZMm = reader.ReadSingle();


                int nMax = WidthPx;
                if (HeightPx > nMax) { HeightPx = nMax; }

                Resolution = 0.007F * (2000.0F / (float)nMax);
                WidthMm = Resolution * (double)WidthPx;
                HeightMm = Resolution * (double)HeightPx;

                var minVal = float.MaxValue;
                var maxVal = float.MinValue;

                // Read all remaining data as 4-byte floats

                ImageData = new float[WidthPx * HeightPx];

                for (var ix = 0; ix < WidthPx * HeightPx; ++ix)
                {
                    var pel = reader.ReadSingle();
                    minVal = Math.Min(pel, minVal);
                    maxVal = Math.Max(pel, maxVal);
                    ImageData[ix] = pel;
                }

                MinZMm = minVal;
                MaxZMm = maxVal;

            }
            else if (fExt == "png")
            {
                System.Drawing.Bitmap img = null;
                try
                {
                    img = new Bitmap(path);  // Use Bitmap to decode png to read its raw data as height data  
                }
                catch
                {
                    IsValid = false;
                    Path = "";
                    MessageBox.Show("Unable to find demo file.  File does not exist.");
                    System.Windows.Application.Current.Shutdown();
                }

                WidthPx = img.Width;
                HeightPx = img.Height;
                ImageData = new float[WidthPx * HeightPx];

                var minVal = float.MaxValue;
                var maxVal = float.MinValue;

                for (int i = 0; i < img.Width; i++)
                {
                    for (int j = 0; j < img.Height; j++)
                    {
                        Color pixel = img.GetPixel(i, j);
                        int ix = j * WidthPx + i;
                        ImageData[ix] = pixel.ToArgb();
                        minVal = Math.Min(ImageData[ix], minVal);
                        maxVal = Math.Max(ImageData[ix], maxVal);
                    }
                }

                MinZMm = minVal;
                MaxZMm = maxVal;

                int nMax = WidthPx;
                if (HeightPx > nMax) { nMax = HeightPx; }

                Resolution = 0.007F * (2000.0F / (float)nMax);
                WidthMm = Resolution * (double)WidthPx;
                HeightMm = Resolution * (double)HeightPx;

                IsValid = true;
            }
            else
            {
                // unknown file ext or type  
                IsValid = false;
                Path = "";
                MessageBox.Show("Unknown file type.");
                System.Windows.Application.Current.Shutdown();
            }

        }

        public double WidthMm { get; }
        public double HeightMm { get; }
        public double Resolution { get; }

        public float this[int x, int y]
        {
            get
            {
                var idx = +(y * WidthPx) + x;
                Debug.Assert(idx >= 0);
                Debug.Assert(idx < ImageData.Length);
                return ImageData[idx];
            }
        }

        public int HeightPx { get; }
        public int WidthPx { get; }
        public double MaxZMm { get; }
        public double MinZMm { get; }
        public string Path { get; private set; }
        public float[] ImageData { get; }
        public bool IsValid { get; }

        private static bool IsValidNumber(double d)
        {
            return !double.IsInfinity(d) && !double.IsNaN(d);
        }

        // https://github.com/ronnieoverby/AsyncBinaryReaderWriter

        public IEnumerable<float> Pixels => ImageData;

        public float GetPel(int y, int x)
        {
            return ImageData[y * WidthPx + x];
        }

        public float GetColMm(int col)
        {
            return (float)(col * Resolution);
        }

        public float GetRowMm(int row)
        {
            var yBase = row * Resolution;
            var yMaxMm = HeightMm;
            var ymm = yMaxMm - yBase;
            return (float)ymm;
        }

        public bool IsEmpty => WidthPx == 0 || HeightPx == 0;

    }

}
