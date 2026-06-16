using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace IssueBuddy
{
    /// <summary>
    /// Modern Windows folder picker (the Vista+ IFileOpenDialog in
    /// folder-picking mode — the same dialog Explorer uses).
    ///
    /// Works identically on net48 and net8-windows with no reference to
    /// System.Windows.Forms and no NuGet packages: it talks to the COM
    /// interface directly.
    /// </summary>
    internal static class FolderPicker
    {
        /// <summary>
        /// Shows the folder picker.
        /// Returns the selected folder path, or null if the user cancelled.
        /// </summary>
        public static string Show(Window owner, string title, string initialDirectory)
        {
            IFileOpenDialog dialog = null;
            try
            {
                dialog = (IFileOpenDialog)new FileOpenDialogRCW();

                // Folder mode, file-system items only.
                dialog.SetOptions(FOS_PICKFOLDERS | FOS_FORCEFILESYSTEM);

                if (!string.IsNullOrEmpty(title))
                    dialog.SetTitle(title);

                // Start in the given folder if it exists.
                if (!string.IsNullOrEmpty(initialDirectory) &&
                    System.IO.Directory.Exists(initialDirectory))
                {
                    IShellItem initialItem;
                    Guid shellItemGuid = typeof(IShellItem).GUID;
                    int hrItem = SHCreateItemFromParsingName(
                        initialDirectory, IntPtr.Zero, ref shellItemGuid, out initialItem);

                    if (hrItem == 0 && initialItem != null)
                    {
                        dialog.SetFolder(initialItem);
                        Marshal.ReleaseComObject(initialItem);
                    }
                }

                IntPtr hwnd = IntPtr.Zero;
                if (owner != null)
                    hwnd = new WindowInteropHelper(owner).Handle;

                int hr = dialog.Show(hwnd);

                // User pressed Cancel.
                if (hr == HRESULT_CANCELLED)
                    return null;

                if (hr != 0)
                    Marshal.ThrowExceptionForHR(hr);

                IShellItem result;
                dialog.GetResult(out result);

                IntPtr pszPath;
                result.GetDisplayName(SIGDN_FILESYSPATH, out pszPath);
                Marshal.ReleaseComObject(result);

                string path = Marshal.PtrToStringUni(pszPath);
                Marshal.FreeCoTaskMem(pszPath);

                return path;
            }
            finally
            {
                if (dialog != null)
                    Marshal.ReleaseComObject(dialog);
            }
        }

        // ── COM plumbing ───────────────────────────────────────────────────

        private const uint FOS_PICKFOLDERS = 0x00000020;
        private const uint FOS_FORCEFILESYSTEM = 0x00000040;
        private const uint SIGDN_FILESYSPATH = 0x80058000;
        private const int HRESULT_CANCELLED = unchecked((int)0x800704C7);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(
            [MarshalAs(UnmanagedType.LPWStr)] string pszPath,
            IntPtr pbc,
            ref Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItem ppv);

        [ComImport]
        [Guid("DC1C5A9C-E88A-4DDE-A5A1-60F82A20AEF7")]   // CLSID FileOpenDialog
        private class FileOpenDialogRCW
        {
        }

        /// <summary>
        /// IFileOpenDialog — methods must stay in exact vtable order.
        /// Unused methods are declared but never called.
        /// </summary>
        [ComImport]
        [Guid("d57c7288-d4ad-4768-be02-9d969532d960")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            // IModalWindow
            [PreserveSig] int Show(IntPtr hwndOwner);

            // IFileDialog
            void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
            void SetFileTypeIndex(uint iFileType);
            void GetFileTypeIndex(out uint piFileType);
            void Advise(IntPtr pfde, out uint pdwCookie);
            void Unadvise(uint dwCookie);
            void SetOptions(uint fos);
            void GetOptions(out uint pfos);
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder(out IShellItem ppsi);
            void GetCurrentSelection(out IShellItem ppsi);
            void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
            void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
            void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
            void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
            void GetResult(out IShellItem ppsi);
            void AddPlace(IShellItem psi, int fdap);
            void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
            void Close(int hr);
            void SetClientGuid(ref Guid guid);
            void ClearClientData();
            void SetFilter(IntPtr pFilter);

            // IFileOpenDialog
            void GetResults(out IntPtr ppenum);
            void GetSelectedItems(out IntPtr ppsai);
        }

        [ComImport]
        [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
            void GetParent(out IShellItem ppsi);
            void GetDisplayName(uint sigdnName, out IntPtr ppszName);
            void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
            void Compare(IShellItem psi, uint hint, out int piOrder);
        }
    }
}
