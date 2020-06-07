using System;
using System.Buffers;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace dllTest
{
    class Program
    {

        //[DllImport("libdav1d.dll")]
        [DllImport("libdav1d.dll", CallingConvention = CallingConvention.Cdecl)]
        private static extern void dav1d_default_settings(ref Dav1dSettings s);

        [DllImport("libdav1d")]
        private static extern int dav1d_open(ref UIntPtr contextOut, ref Dav1dSettings s);

        [DllImport("libdav1d")]
        private static extern int dav1d_get_picture(UIntPtr dav1dContext, out Dav1dPicture picture);

        [DllImport("libdav1d")]
        private static extern unsafe int dav1d_data_wrap(ref Dav1dData data, byte* buffer, UIntPtr size,
            FreeCallback freeCallback, IntPtr cookie);

        [DllImport("libdav1d")]
        private static extern int dav1d_send_data(UIntPtr context, ref Dav1dData @in);

        [DllImport("libdav1d.dll")]
        private static extern IntPtr dav1d_data_create(ref Dav1dData data, UIntPtr sz);

        [DllImport("libdav1d.dll")]
        private static extern void dav1d_picture_unref(ref Dav1dPicture p);

        [DllImport("cppdll.dll")]
        private static extern unsafe int dav1d_data_wrap_internal(ref Dav1dData data, byte* buffer, UIntPtr size,
            FreeCallback freeCallback, IntPtr cookie);

        [DllImport("cppdll.dll")]
        private static extern unsafe void AddOneToByteArray(byte* byteArray, ulong length);

        public const int EAgain = -11;

        public static int frameNumber;

        static void Main(string[] args)
        {
            Console.WriteLine("Hello World!");

            var setting = new Dav1dSettings();
            dav1d_default_settings(ref setting);

            setting.nFrameThreads = 4;
            setting.nTileThreads = 4;

            var contextOut = new UIntPtr();
            var result = dav1d_open(ref contextOut, ref setting);

            var fileBytes = File.ReadAllBytes(@"D:\ffmpeg-labs\output-0.ivf")
                .AsMemory()
                .Slice(32);
            var stream = new MemoryStream(fileBytes.ToArray());

            var stopWatch = new Stopwatch();
            stopWatch.Start();
            while (true)
            {
                var headerBytes = new byte[12];

                var received = stream.Read(headerBytes); // TODO : Make sure read 12 byte always
                // Console.WriteLine($"Header Received : {received}");
                if (received == 0)
                {
                    break;
                }

                var size = BitConverter.ToInt32(headerBytes);
                // Console.WriteLine($"Size : {size}");

                var bytes = new byte[size];
                received = stream.Read(bytes);
                // Console.WriteLine($"Frame Received : {received}");
                if (received == 0)
                {
                    break;
                }

                var bufferPtr = CreateData(size, out var data);

                Marshal.Copy(bytes, 0, bufferPtr, received);

                // SendData(contextOut, memoryOwner.Memory.Slice(0, received).ToArray());
                SendData(contextOut, data);
                if (TryGetNextFrameTexture(contextOut, out var picture))
                {
                    OnPicture(picture);
                }

                if (frameNumber == 1200)
                {
                    break;
                }
            }
            while (true)
            {
                if (frameNumber == 1200)
                {
                    break;
                }

                if (TryGetNextFrameTexture(contextOut, out var picture))
                {
                    OnPicture(picture);
                }
            }
            stopWatch.Stop();

            Console.WriteLine(stopWatch.Elapsed);

        }

        public static void OnPicture(Dav1dPicture picture)
        {
            Console.WriteLine($"Frame Number {frameNumber++}");
            //var bitmap = ConvertPictureToBitmap(picture);
            //bitmap.Save("result.bmp");
            dav1d_picture_unref(ref picture);
        }

        private static IntPtr CreateData(int size, out Dav1dData data)
        {
            data = new Dav1dData();
            return dav1d_data_create(ref data, new UIntPtr((uint)size));
        }

        public static void SendData(UIntPtr context, Dav1dData data)
        {
            var result = dav1d_send_data(context, ref data);
            if (result < 0 && result != EAgain)
            {
                throw new Exception($"fail to send data {result}");
            }
        }

        public static void SendData(UIntPtr context, byte[] buffer)
        {
            unsafe
            {
                fixed (byte* bytes = buffer)
                {
                    var data = new Dav1dData
                    {
                        m = new Dav1dDataProps(),
                    };

                    var result = dav1d_data_wrap(ref data, bytes, (UIntPtr)buffer.Length, FreeCallback, IntPtr.Zero);
                    if (result != 0)
                    {
                        throw new Exception($"fail to wrap data {result}");
                    }

                    result = dav1d_send_data(context, ref data);
                    if (result != 0)
                    {
                        throw new Exception($"fail to send data {result}");
                    }
                }
            }
        }

        private static unsafe void FreeCallback(byte* data, void* _)
        {
            Console.WriteLine("FREE");
        }

        private static (int r, int g, int b) convertYUVtoRGB(int y, int u, int v)
        {
            var c = y - 16;
            var d = u - 128;
            var e = v - 128;

            var r = (298 * c + 409 * e + 128) >> 8;
            var g = (298 * c - 100 * d - 208 * e + 128) >> 8;
            var b = (298 * c + 516 * d + 128) >> 8;

            r = r > 255 ? 255 : r < 0 ? 0 : r;
            g = g > 255 ? 255 : g < 0 ? 0 : g;
            b = b > 255 ? 255 : b < 0 ? 0 : b;

            return (r, g, b);
        }

        public static Bitmap ConvertPictureToBitmap(Dav1dPicture picture)
        {
            var bitmap = new Bitmap(1920, 1080);
            for (var y = 0; y < bitmap.Height; y += 1)
            {
                for (var x = 0; x < bitmap.Width; x += 1)
                {
                    bitmap.SetPixel(x, y, Color.White);
                }
            }

            unsafe
            {
                var lumaLength = picture._stride[0].ToUInt32();
                var lumaBytesPtr = (byte*)picture._data[0].ToPointer();
                var uBytesPtr = (byte*)picture._data[1].ToPointer();
                var vBytesPtr = (byte*)picture._data[2].ToPointer();

                const int width = 1920;
                const int height = 1080;

                for (var i = 0; i < width * height / 4; i += 1)
                {
                    // y0 y1
                    // y2 y3

                    var x = i % (width / 2) * 2;
                    var y = i / (width / 2) * 2;

                    var y0 = lumaBytesPtr[x + y * width];
                    var y1 = lumaBytesPtr[x + 1 + y * width];
                    var y2 = lumaBytesPtr[x + y * width];
                    var y3 = lumaBytesPtr[x + 1 + (y + 1) * width];

                    var u = uBytesPtr[i];
                    var v = vBytesPtr[i];

                    var color0 = convertYUVtoRGB(y0, u, v);
                    var color1 = convertYUVtoRGB(y1, u, v);
                    var color2 = convertYUVtoRGB(y2, u, v);
                    var color3 = convertYUVtoRGB(y3, u, v);

                    bitmap.SetPixel(x, y, Color.FromArgb(color0.r, color0.g, color0.b));
                    bitmap.SetPixel(x + 1, y, Color.FromArgb(color1.r, color1.g, color1.b));
                    bitmap.SetPixel(x, y + 1, Color.FromArgb(color2.r, color2.g, color2.b));
                    bitmap.SetPixel(x + 1, y + 1, Color.FromArgb(color3.r, color3.g, color3.b));
                }
            }

            return bitmap;
        }

        public static bool TryGetNextFrameTexture(UIntPtr context, out Dav1dPicture picture)
        {
            var result = dav1d_get_picture(context, out picture);
            if (result < 0)
            {
                if (result == EAgain)
                {
                    return false;
                }

                throw new Exception($"fail to get next frame {result}");
            }

            return true;
        }
    }
}
