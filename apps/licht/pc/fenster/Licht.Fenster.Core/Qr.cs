using Net.Codecrete.QrCodeGenerator;

namespace Licht.Fenster
{
    /// <summary>QR codes for sharing lamps (error correction M: still readable from a slightly blurry screen photo).</summary>
    public static class Qr
    {
        /// <summary>The modules of the QR code for a text: [y, x], true = dark. Without the quiet zone.</summary>
        public static bool[,] Matrix(string text)
        {
            var code = QrCode.EncodeText(text, QrCode.Ecc.Medium);
            var matrix = new bool[code.Size, code.Size];
            for (var y = 0; y < code.Size; y++)
            for (var x = 0; x < code.Size; x++)
                matrix[y, x] = code.GetModule(x, y);
            return matrix;
        }
    }
}
