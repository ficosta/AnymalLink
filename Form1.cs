using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Timers;
using System.Windows.Forms;
using CefSharp;
using CefSharp.OffScreen;

namespace AnymalLink
{
    public partial class Form1 : Form
    {
        private ChromiumWebBrowser browser;

        // Variáveis para gerenciamento do Timer

        private int frameCount = 0;
        private int currentFPS = 0;
        private readonly object frameLock = new object();
        private System.Timers.Timer fpsTimer;

        private Bitmap latestBitmap;
        private readonly object bitmapLock = new object();

        private Process ffmpegProcess;
        private Stream ffmpegInputStream;

        public Form1()
        {
            InitializeComponent();
            InitializeCefSharpAsync();
            InitializeFFmpeg();

        }
        private void InitializeFFmpeg()
        {
            // Configurar o processo FFmpeg
            var ffmpegPath =  "ffmpeg.exe"; // Certifique-se de que ffmpeg.exe está na pasta do executável
            var rtmpUrl = $"rtmp://a.rtmp.youtube.com/live2/mzgp-b1ur-agvu-py65-bjux";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                Arguments = $"-f rawvideo -pix_fmt bgr24 -s 1920x1080 -r 30 -i - -c:v libx264 -pix_fmt yuv420p -preset veryfast -f flv {rtmpUrl}",
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            ffmpegProcess = new Process { StartInfo = startInfo };
            ffmpegProcess.Start();

            ffmpegInputStream = ffmpegProcess.StandardInput.BaseStream;

            // Opcional: Ler o erro do FFmpeg para debug
            ffmpegProcess.ErrorDataReceived += (sender, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    Console.WriteLine($"FFmpeg: {e.Data}");
                }
            };
            ffmpegProcess.BeginErrorReadLine();
        }

        private async void InitializeCefSharpAsync()
        {
            var settings = new CefSettings()
            {
                WindowlessRenderingEnabled = true,
                CachePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CefSharp\\Cache")
            };

            // Inicializa o Cef de forma assíncrona
            await Cef.InitializeAsync(settings, performDependencyCheck: true, browserProcessHandler: null);

            // Cria a instância do navegador off-screen apontando para uma página em branco
            browser = new ChromiumWebBrowser("about:blank")
            {
                Size = new Size(1920, 1080) // Define a resolução desejada
            };

            // Assina o evento OnPaint
            browser.Paint += OnBrowserPaint;

            // Configurar o Timer para calcular FPS
            fpsTimer = new System.Timers.Timer(1000); // 1 segundo
            fpsTimer.Elapsed += FpsTimer_Elapsed;
            fpsTimer.AutoReset = true;
            fpsTimer.Start();
        }

        private Bitmap reusableBitmap;
        private Bitmap alphaBitmap;

        private void OnBrowserPaint(object sender, OnPaintEventArgs e)
        {
            int bytes = e.Width * e.Height * 3; // bgr24
            byte[] pixelData = new byte[bytes];
            IntPtr bufferPtr = e.BufferHandle;

            // Converter de RGBA para BGR24
            unsafe
            {
                byte* src = (byte*)bufferPtr.ToPointer();
                fixed (byte* dstFixed = pixelData)
                {
                    byte* dst = dstFixed;
                    for (int i = 0; i < e.Width * e.Height; i++)
                    {
                        dst[0] = src[2]; // B
                        dst[1] = src[1]; // G
                        dst[2] = src[0]; // R
                        src += 4; // Pular o canal alpha
                        dst += 3;
                    }
                }
            }

            // Enviar os dados para o FFmpeg
            try
            {
                if (ffmpegInputStream != null && ffmpegInputStream.CanWrite)
                {
                    ffmpegInputStream.Write(pixelData, 0, pixelData.Length);
                    ffmpegInputStream.Flush();
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Erro ao escrever no FFmpeg: {ex.Message}");
            }

            // Atualizar os PictureBoxes RGB e Alpha
            UpdatePictureBoxes(e, pixelData);

            // Incrementar o contador de frames de forma thread-safe
            lock (frameLock)
            {
                frameCount++;
            }
        }

        private void UpdatePictureBoxes(OnPaintEventArgs e, byte[] pixelData)
        {
            // Atualizar reusableBitmap (RGB)
            if (reusableBitmap == null || reusableBitmap.Width != e.Width || reusableBitmap.Height != e.Height)
            {
                reusableBitmap?.Dispose();
                reusableBitmap = new Bitmap(e.Width, e.Height, PixelFormat.Format24bppRgb);
            }

            BitmapData bitmapData = reusableBitmap.LockBits(new Rectangle(0, 0, e.Width, e.Height),
                                                           ImageLockMode.WriteOnly,
                                                           PixelFormat.Format24bppRgb);
            try
            {
                Marshal.Copy(pixelData, 0, bitmapData.Scan0, pixelData.Length);
            }
            finally
            {
                reusableBitmap.UnlockBits(bitmapData);
            }

            // Atualizar alphaBitmap (Canal Alpha em B&W)
            // Extraindo o canal alpha original
            int alphaBytes = e.Width * e.Height;
            byte[] alphaData = new byte[alphaBytes];
            IntPtr bufferPtr = e.BufferHandle;

            unsafe
            {
                byte* src = (byte*)bufferPtr.ToPointer();
                for (int i = 0; i < alphaBytes; i++)
                {
                    alphaData[i] = src[3]; // Canal alpha
                    src += 4; // Pular para o próximo pixel
                }
            }

            if (alphaBitmap == null || alphaBitmap.Width != e.Width || alphaBitmap.Height != e.Height)
            {
                alphaBitmap?.Dispose();
                alphaBitmap = new Bitmap(e.Width, e.Height, PixelFormat.Format8bppIndexed);

                // Configurar a paleta para escala de cinza
                ColorPalette palette = alphaBitmap.Palette;
                for (int i = 0; i < 256; i++)
                {
                    palette.Entries[i] = Color.FromArgb(i, i, i);
                }
                alphaBitmap.Palette = palette;
            }

            BitmapData alphaBitmapData = alphaBitmap.LockBits(new Rectangle(0, 0, e.Width, e.Height),
                                                              ImageLockMode.WriteOnly,
                                                              PixelFormat.Format8bppIndexed);
            try
            {
                Marshal.Copy(alphaData, 0, alphaBitmapData.Scan0, alphaData.Length);
            }
            finally
            {
                alphaBitmap.UnlockBits(alphaBitmapData);
            }

            // Clonar os bitmaps para evitar problemas de threading
            Bitmap clonedBitmap = (Bitmap)reusableBitmap.Clone();
            Bitmap clonedAlphaBitmap = (Bitmap)alphaBitmap.Clone();

            // Atualizar os PictureBoxes na Thread de UI
            this.Invoke(new Action(() =>
            {
                // Atualizar o PictureBox RGB
                pictureBoxScreenshot.Image?.Dispose();
                pictureBoxScreenshot.Image = clonedBitmap;

                // Atualizar o PictureBox Alpha
                pictureBoxAlpha.Image?.Dispose();
                pictureBoxAlpha.Image = clonedAlphaBitmap;
            }));
        }



        private async void btnCapture_Click(object sender, EventArgs e)
        {
            string url = "https://app.overlays.uno/output/11jC4IZWNdSxQnBrT9Tq0H?aspect=16x9";

            if (string.IsNullOrEmpty(url))
            {
                MessageBox.Show("Por favor, insira uma URL válida.", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            // Valida a URL
            if (!Uri.IsWellFormedUriString(url, UriKind.Absolute))
            {
                MessageBox.Show("A URL inserida não é válida.", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }

            btnCapture.Enabled = false;
            btnCapture.Text = "Capturando...";

            try
            {
                await LoadPageAsync(url);
 }
            catch (Exception ex)
            {
                MessageBox.Show($"Ocorreu um erro:\n{ex.Message}", "Erro", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                btnCapture.Enabled = true;
                btnCapture.Text = "Capturar Screenshot";
            }
        }

        private async Task LoadPageAsync(string url)
        {
            var tcs = new TaskCompletionSource<bool>();

            void OnLoadingStateChanged(object sender, LoadingStateChangedEventArgs args)
            {
                if (!args.IsLoading)
                {
                    browser.LoadingStateChanged -= OnLoadingStateChanged;
                    tcs.TrySetResult(true);
                }
            }

            browser.LoadingStateChanged += OnLoadingStateChanged;
            browser.Load(url);

            await tcs.Task;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            fpsTimer?.Stop();
            fpsTimer?.Dispose();

            browser.Paint -= OnBrowserPaint;
            browser?.Dispose();

            if ((bool)Cef.IsInitialized)
            {
                Cef.Shutdown();
            }

            base.OnFormClosing(e);
        }

        private void UpdateTimer_Tick(object sender, EventArgs e)
        {
            Bitmap bitmapToDisplay = null;

            lock (bitmapLock)
            {
                if (latestBitmap != null)
                {
                    bitmapToDisplay = new Bitmap(latestBitmap);
                    latestBitmap.Dispose();
                    latestBitmap = null;
                }
            }

            if (bitmapToDisplay != null)
            {
                // Atualizar o PictureBox na Thread de UI
                pictureBoxScreenshot.Image?.Dispose();
                pictureBoxScreenshot.Image = bitmapToDisplay;
            }
        }
        private void FpsTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            int fps;

            // Capturar o frameCount de forma thread-safe
            lock (frameLock)
            {
                fps = frameCount;
                frameCount = 0;
            }

            // Atualizar o Label na Thread de UI
            if (lblFPS.InvokeRequired)
            {
                lblFPS.Invoke(new Action(() =>
                {
                    lblFPS.Text = $"FPS: {fps}";
                }));
            }
            else
            {
                lblFPS.Text = $"FPS: {fps}";
            }
        }

    }
}
