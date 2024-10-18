using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Policy;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using CefSharp;
using CefSharp.OffScreen;

namespace AnymalLink
{
    public partial class Form1 : Form
    {
        private ChromiumWebBrowser browser;

        // Variáveis para gerenciamento do Timer
        private Bitmap latestBitmap;
        private readonly object bitmapLock = new object();

        public Form1()
        {
            InitializeComponent();
            InitializeCefSharpAsync();
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
        }

        private void OnBrowserPaint(object sender, OnPaintEventArgs e)
        {
            // Calcula o número de bytes (Width * Height * 4 para RGBA)
            int bytes = e.Width * e.Height * 4;

            // Cria um array de bytes para armazenar os dados de pixel
            byte[] pixelData = new byte[bytes];

            // Copia os dados do buffer não gerenciado para o array de bytes
            System.Runtime.InteropServices.Marshal.Copy(e.BufferHandle, pixelData, 0, bytes);

            // Cria um Bitmap a partir dos dados de pixel
            using (var bitmap = new Bitmap(e.Width, e.Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
            {
                var bitmapData = bitmap.LockBits(new Rectangle(0, 0, e.Width, e.Height),
                                                System.Drawing.Imaging.ImageLockMode.WriteOnly,
                                                System.Drawing.Imaging.PixelFormat.Format32bppPArgb);

                try
                {
                    System.Runtime.InteropServices.Marshal.Copy(pixelData, 0, bitmapData.Scan0, bytes);
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }

                // Clona o Bitmap para evitar problemas de threading
                var clonedBitmap = new Bitmap(bitmap);

                // Atualiza o PictureBox na Thread de UI
                this.Invoke(new Action(() =>
                {
                    // Desfaz a imagem anterior para evitar vazamentos de memória
                    pictureBoxScreenshot.Image?.Dispose();
                    pictureBoxScreenshot.Image = clonedBitmap;
                }));
            }
        }
        private async void btnCapture_Click(object sender, EventArgs e)
        {
            string url = "https://upload.wikimedia.org/wikipedia/commons/transcoded/c/c0/Big_Buck_Bunny_4K.webm/Big_Buck_Bunny_4K.webm.1080p.vp9.webm";

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
            //browser?.Paint -= OnBrowserPaint;
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
    }
}
