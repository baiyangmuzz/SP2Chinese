using System;using System.Diagnostics;using System.Drawing;using System.Drawing.Imaging;using System.Runtime.InteropServices;using System.Threading;
class Cap{
 [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
 [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
 [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
 [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
 [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h,int c);
 [StructLayout(LayoutKind.Sequential)] public struct RECT{public int L,T,R,B;}
 [STAThread] static int Main(string[] a){
  SetProcessDPIAware();int pid=int.Parse(a[0]);IntPtr hwnd=IntPtr.Zero;
  for(int i=0;i<80;i++){try{var p=Process.GetProcessById(pid);p.Refresh();if(p.MainWindowHandle!=IntPtr.Zero){hwnd=p.MainWindowHandle;break;}}catch{} Thread.Sleep(250);}
  if(hwnd==IntPtr.Zero){Console.WriteLine("no window");return 1;}
  ShowWindow(hwnd,5);BringWindowToTop(hwnd);SetForegroundWindow(hwnd);Thread.Sleep(3000);
  RECT r;GetWindowRect(hwnd,out r);int w=r.R-r.L,h=r.B-r.T;
  using(var bmp=new Bitmap(w,h))using(var g=Graphics.FromImage(bmp)){g.CopyFromScreen(r.L,r.T,0,0,new Size(w,h));bmp.Save(a[1],ImageFormat.Png);}
  Console.WriteLine("saved "+w+"x"+h);return 0;}
}
