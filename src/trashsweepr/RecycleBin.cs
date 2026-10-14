using System.Runtime.InteropServices;

namespace TrashSweepr;

/// <summary>
/// Move para a Lixeira pelo próprio shell do Windows (mesmo caminho do Explorer): dá para restaurar
/// depois, e se o item não couber na Lixeira o Windows avisa antes de apagar de vez.
/// </summary>
public static class RecycleBin
{
    const uint FO_DELETE = 3;
    const ushort FOF_NOCONFIRMATION = 0x0010;
    const ushort FOF_ALLOWUNDO = 0x0040;
    const ushort FOF_WANTNUKEWARNING = 0x4000;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        [MarshalAs(UnmanagedType.Bool)] public bool fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Resultado de sucesso de <see cref="SendAsync"/>.</summary>
    public const int Ok = 0;

    /// <summary>ERROR_CANCELLED: devolvido também quando o usuário interrompe a operação no diálogo do shell.</summary>
    public const int Cancelled = 1223;
    const int DE_OPCANCELLED = 0x75;

    /// <summary>MAX_PATH: o SHFileOperation não aceita caminhos deste tamanho em diante.</summary>
    const int MaxPath = 260;

    public static bool IsCancelled(int code) => code is Cancelled or DE_OPCANCELLED;

    public static bool IsTooLong(string path) => path.Length >= MaxPath;

    /// <summary>
    /// Roda numa thread STA própria: o shell mostra o progresso dele sem travar a janela.
    /// Devolve o código do SHFileOperation (<see cref="Ok"/> se tudo foi movido, <see cref="Cancelled"/> se o usuário interrompeu).
    /// </summary>
    public static Task<int> SendAsync(string path, IntPtr owner)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var op = new SHFILEOPSTRUCT
                {
                    hwnd = owner,
                    wFunc = FO_DELETE,
                    pFrom = path + "\0\0", // lista terminada por nulo duplo
                    fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_WANTNUKEWARNING,
                };
                int result = SHFileOperation(ref op);
                tcs.SetResult(result == Ok && op.fAnyOperationsAborted ? Cancelled : result);
            }
            catch (Exception e)
            {
                tcs.SetException(e);
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        return tcs.Task;
    }
}
