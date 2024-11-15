using System;
using System.Diagnostics;  // Process 類所在命名空間
using System.Net;
using System.Reflection.Emit;
using System.Runtime.InteropServices;
using System.IO;
using System.Timers;
using System.Threading;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using static cnc_gui.Focas1;
using System.Data;
using MySql.Data.MySqlClient;
using LiveCharts.Maps;
using System.Windows;
using static Mysqlx.Notice.Warning.Types;
using static Mysqlx.Datatypes.Scalar.Types;
namespace cnc_gui
{
    public class core
    {
        private readonly object lockObj = new object();
        public static bool OnOff = true;   // 用來控制是否繼續運行
        public static Thread mainThread = null;  // 保存運行的線程
        public static Thread excluderThread = null;// 用於執行排屑機的線程
        public static Thread RdspmeterThread = null;// 用於執行主軸負載的線程
        public static CancellationTokenSource cancellationTokenSource; // 用來取消任務的標記
        private static int T = 0;  // 計數器
        private static int C = 0;//平台換算成積屑等級
        private static int CurrentParam = 5; //目前排屑機所帶入的c值
        private short FIdCode;  //cnc沖水點位idcord
        public static short EIdCode;  //cnc排屑點位idcord
        public static ushort Fdatano; //cnc沖水點位address
        public static ushort Edatano; //cnc沖水點位address
        private static int Excluder_Period;//排屑機啟動週期
        public static long spindle;
        public DatabaseModel databaseModel;
        public static short connection_r4;//改變燈號
        private string _cncIp;
        private ushort _cncPort;
        public core()
        {
            databaseModel = new DatabaseModel();
            LoadData();
        }

        public static void MainProcessing(bool start)
        {
            var mainCore = new core();
            var home = new home();
            if (start)
            {
                if (mainThread == null || !mainThread.IsAlive)
                {
                    if (cancellationTokenSource == null || cancellationTokenSource.IsCancellationRequested)
                    {
                        cancellationTokenSource = new CancellationTokenSource();
                    }
                    var token = cancellationTokenSource.Token;
                    OnOff = true;
                    // 開啟背景執行的主任務
                    mainThread = new Thread(() =>
                    {
                        while (OnOff && !token.IsCancellationRequested)
                        {
                            ushort FFlibHndl1;
                            short R = Focas1.cnc_allclibhndl3(mainCore._cncIp, mainCore._cncPort, 1, out FFlibHndl1);
                            Stopwatch stopwatch = new Stopwatch();
                            stopwatch.Start();
                            mainCore.ImageProcess();  // 拍照+AI
                            int level = mainCore.databaseModel.GetLevelResult().Flusher_level_result;
                            mainCore.Flusher(level, Fdatano, Fdatano, mainCore.FIdCode, FFlibHndl1);

                            lock (mainCore.lockObj)
                            {
                                C += level;

                                if (T < 4)
                                {
                                    T += 1;
                                }
                                else if (T == 4)
                                {
                                    T = 0;
                                    CurrentParam = C;
                                    C = 0;
                                }
                            }
                            stopwatch.Stop();
                            int remainingTime = 100000 - (int)stopwatch.ElapsedMilliseconds;
                            if (remainingTime > 0)
                            {
                                SpinWait.SpinUntil(() => false, remainingTime);
                            }
                        }
                    });
                    mainThread.Start();
                    // 排屑機執行緒
                    excluderThread = new Thread(() =>
                    {

                        while (OnOff && !token.IsCancellationRequested)
                        {
                            ushort FFlibHndl2;
                            short R2 = Focas1.cnc_allclibhndl3(mainCore._cncIp, mainCore._cncPort, 1, out FFlibHndl2);
                            int currentParam;//當前currentParam值，避免出現競爭
                            lock (mainCore.lockObj)
                            {
                                currentParam = CurrentParam;
                            }
                            mainCore.Excluder(currentParam, Fdatano, Fdatano, mainCore.FIdCode, FFlibHndl2);
                        }
                    });
                    excluderThread.Start();
                    // 開啟主軸負載執行緒
                    RdspmeterThread = new Thread(() =>
                    {
                        while (OnOff && !token.IsCancellationRequested)
                        {
                            ushort FFlibHndl3;
                            short R3 = Focas1.cnc_allclibhndl3(mainCore._cncIp, mainCore._cncPort, 1, out FFlibHndl3);
                            long get = mainCore.GetData(FFlibHndl3);
                            spindle = get;
                            Thread.Sleep(1000);  //每秒檢查一次

                        }
                    });
                    RdspmeterThread.Start();
                }
            }
            else
            {
                if (mainThread != null && mainThread.IsAlive)
                {
                    //關掉程式
                    OnOff = false;
                    cancellationTokenSource.Cancel();
                    mainThread.Join(1000);  
                    excluderThread.Join(1000);
                    RdspmeterThread.Join(1000);
                    mainThread = null;
                    excluderThread = null;
                    RdspmeterThread = null;
                }
            }
        }
        //讀取database資料
        private void LoadData()
        {
            _cncIp = databaseModel.GetIp_Port().Cncip;
            _cncPort = StringToUshort(databaseModel.GetIp_Port().Cncport);
            FIdCode = (short)databaseModel.GetPlcControlById(1).Handl;
            EIdCode = (short)databaseModel.GetPlcControlById(2).Handl;
            Fdatano = (ushort)databaseModel.GetPlcControlById(1).Address;
            Excluder_Period = databaseModel.GetSetting().Excluder_Period;
        }
        //沖水按鈕
        public void TestFlusher()
        {
            ushort FFlibHndl4;
            short R4 = Focas1.cnc_allclibhndl3(_cncIp, _cncPort, 1, out FFlibHndl4);
            WritePmcData((ushort)databaseModel.GetPlcControlById(1).Address, (ushort)databaseModel.GetPlcControlById(1).Address, 1, FIdCode = (short)databaseModel.GetPlcControlById(1).Handl, FFlibHndl4);
        }
        public static int StringToInt(string inString)
        {
            int.TryParse(inString, out int result);
            return result;
        }

        //string轉成ushort
        public static ushort StringToUshort(string inString)
        {
            ushort.TryParse(inString, out ushort result);
            return result;
        }
        //string轉乘short
        public static short StringToShort(string inString)
        {
            short.TryParse(inString, out short result);
            return result;

        }
        //影像處理+AI
        void ImageProcess()
        {
            // 設定 Python檔絕對路徑
            string pythonFilePath = @"D:\cnc_gui_s-master\method_AI\take_pic.py";
            ProcessStartInfo psi = new ProcessStartInfo();
            psi.FileName = @"C:\Program Files\Python311\python.exe";  //exe路徑
            psi.Arguments = pythonFilePath;
            psi.CreateNoWindow = true;                 // 不顯示命令行視窗
            psi.UseShellExecute = false;               // 必須設為 false，以便不重定向輸出
            psi.RedirectStandardOutput = true;         // 不需要捕捉標準輸出
            psi.RedirectStandardError = true;          // 不需要捕捉標準錯誤
            try
            {
                using (Process process = Process.Start(psi))
                {
                    process.WaitForExit();
                }
            }
            catch (Exception ex)
            {
            }
        }
        //讀取主軸負載
        public long GetData(ushort FFlibHnd1)
        {
            long data = -1;
            short data_num = 1;
            Focas1.Odbspload spindleLoad = new Focas1.Odbspload();
            var ret = Focas1.cnc_rdspmeter(FFlibHnd1, 0, ref data_num, spindleLoad);

            if (ret == Focas1.EW_OK)
            {
                data = spindleLoad.spload_data.spload.data;


            }
            else
            {
                Console.WriteLine("failed to read");
            }
            return data;
        }

        //10進制轉2進制陣列
        int[] ConvertToBinaryArray(int decimalNumber)
        {
            int[] binaryarr = new int[8];
            for (int i = 0; i < 8; i++)
            {
                binaryarr[7 - i] = (decimalNumber >> i) & 1;
            }
            return binaryarr;
        }

        //2進制轉10進制
        int ConvertBinaryArrayToDecimal(int[] binaryArray)
        {
            int decimalValue = 0;
            int length = binaryArray.Length;
            for (int i = 0; i < length; i++)
            {
                if (binaryArray[i] != 0 && binaryArray[i] != 1)
                    throw new FormatException("錯誤的二進制數值。");
                decimalValue += binaryArray[length - 1 - i] * (int)Math.Pow(2, i);
            }
            return decimalValue;
        }


        //讀取十進制數值，之前資料型態是long
        public int ReadByteParam(ushort datano_s, ushort datano_e, short IdCode, ushort FFlibHndl) //起始位置，結束位置，idcode
        {
            ushort length = (ushort)(8 + (datano_e - datano_s + 1));
            Focas1.Iodbpmc buf = new Focas1.Iodbpmc();
            short ret = Focas1.pmc_rdpmcrng(FFlibHndl, IdCode, 0, datano_e, datano_s, length, buf);
            return buf.cdata[0];
        }
        //寫入修改好的10進制數值，要修改的時候就呼叫一次
        public void WritePmcData(ushort datano_s, ushort datano_e, int i, short IdCode, ushort FFlibHndl) //起始位置，結束位置，i=要修改的bit，idcode
        {
            ReadByteParam(datano_s, datano_e, IdCode, FFlibHndl);
            ushort length = (ushort)(8 + (datano_e - datano_s + 1));
            Focas1.Iodbpmc buf = new Focas1.Iodbpmc();
            short ret = Focas1.pmc_rdpmcrng(FFlibHndl, IdCode, 0, datano_e, datano_s, length, buf);
            int[] binaryArray = ConvertToBinaryArray(buf.cdata[0]); //10轉2
            if (binaryArray.Length > 0)
            {
                int machineIndex = binaryArray.Length - 1 - i; // 映射i到機器的位址
                binaryArray[machineIndex] = binaryArray[machineIndex] == 0 ? 1 : 0;
            }
            int modifiedDecimalValue = ConvertBinaryArrayToDecimal(binaryArray);//2轉10
            buf.cdata[0] = (byte)modifiedDecimalValue;
            short rt = Focas1.pmc_wrpmcrng(FFlibHndl, (short)length, buf);
        }
        //底座環沖控制
        public void Flusher(int level, ushort datano_s, ushort datano_e, short IdCode, ushort FFlibHndl)
        {
            if (level == 1)
            {
                //level1不沖水
            }

            else if (level == 2)
            {
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
                Thread.Sleep(3500);
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
            }
            else if (level == 3)
            {
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
                Thread.Sleep(4500);
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
            }
            else if (level == 4)
            {
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
                Thread.Sleep(6500);
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
            }
            else if (level == 5)
            {
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
                Thread.Sleep(7500);
                WritePmcData(datano_s, datano_e, 1, IdCode, FFlibHndl);
            }
        }
        //排屑機控制
        void Excluder(int c, ushort datano_s, ushort datano_e, short IdCode, ushort FFlibHndl)
        {
            if (c == Excluder_Period)
            {
                databaseModel.UpdateLevelResult("Excluder_level_result", 1);
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                Thread.Sleep(100);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                stopwatch.Stop();
                int remainingTime = 1000 - (int)stopwatch.ElapsedMilliseconds;
                if (remainingTime > 0)
                {
                    SpinWait.SpinUntil(() => false, remainingTime);
                }
            }
            else if (c > Excluder_Period && c < (Excluder_Period * 2) + 1)
            {
                databaseModel.UpdateLevelResult("Excluder_level_result", 2);
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                Thread.Sleep(300);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                stopwatch.Stop();
                int remainingTime = 1000 - (int)stopwatch.ElapsedMilliseconds;
                if (remainingTime > 0)
                {
                    SpinWait.SpinUntil(() => false, remainingTime);
                }
            }
            else if (c > (Excluder_Period * 2) && c < (Excluder_Period * 3) + 1)
            {
                databaseModel.UpdateLevelResult("Excluder_level_result", 3);
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                Thread.Sleep(500);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                stopwatch.Stop();
                int remainingTime = 1000 - (int)stopwatch.ElapsedMilliseconds;
                if (remainingTime > 0)
                {
                    SpinWait.SpinUntil(() => false, remainingTime);
                }
            }
            else if (c > (Excluder_Period * 3) && c < (Excluder_Period * 4) + 1)
            {
                databaseModel.UpdateLevelResult("Excluder_level_result", 4);
                Stopwatch stopwatch = new Stopwatch();
                stopwatch.Start();
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                Thread.Sleep(700);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                stopwatch.Stop();
                int remainingTime = 1000 - (int)stopwatch.ElapsedMilliseconds;
                if (remainingTime > 0)
                {
                    SpinWait.SpinUntil(() => false, remainingTime);
                }
            }
            else if (c > (Excluder_Period * 4) && c < (Excluder_Period * 5) + 1)
            {
                databaseModel.UpdateLevelResult("Excluder_level_result", 5);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
                Thread.Sleep(1000);
                WritePmcData(datano_s, datano_e, 0, IdCode, FFlibHndl);
            }
        }
    }
    public class Focas1
    {
        // Declare constants and methods from FOCAS library
        public const short EW_OK = 0;
        [DllImport("./Fwlib32.dll")]
        public static extern short cnc_allclibhndl3(string ip, ushort port, int timeout, out ushort libhndl);

        [DllImport("./Fwlib32.dll")]
        public static extern short cnc_freelibhndl(ushort libhndl);

        [DllImport("./Fwlib32.dll")]
        public static extern short cnc_rdspmeter(ushort libhndl, short type, ref short data_num, [MarshalAs(UnmanagedType.LPStruct), Out] Odbspload spmeter);

        [DllImport("./Fwlib32.dll")]
        public static extern short pmc_rdpmcrng(ushort FlibHndl, short adr_type, short data_type, ushort s_number, ushort e_number, ushort length, [MarshalAs(UnmanagedType.LPStruct), Out] Iodbpmc buf);

        [DllImport("./Fwlib32.dll")]
        public static extern short pmc_wrpmcrng(ushort FlibHndl, short length, [MarshalAs(UnmanagedType.LPStruct), In] Iodbpmc buf);
        [StructLayout(LayoutKind.Sequential)]

        public class Odbspload
        {
            public Odbspload_data spload_data = new Odbspload_data();
        }
        [StructLayout(LayoutKind.Sequential)]
        public class Odbspload_data
        {
            public Loadlm spload = new Loadlm();
            public Loadlm spspeed = new Loadlm();
        }
        [StructLayout(LayoutKind.Sequential)]
        public class Loadlm
        {
            public long data;       /* load meter data, motor speed */
            public short dec;        /* place of decimal point */
            public short unit;       /* unit */
            public char name;       /* spindle name */
            public char suff1;      /* subscript of spindle name 1 */
            public char suff2;      /* subscript of spindle name 2 */
            public char reserve;    /* */

        }

        [StructLayout(LayoutKind.Explicit)]
        public class Iodbpmc
        {
            [FieldOffset(0)]
            public short type_a;   /* Kind of PMC address */
            [FieldOffset(2)]
            public short type_d;   /* Type of the PMC data */
            [FieldOffset(4)]
            public ushort datano_s; /* Start PMC address number */
            [FieldOffset(6)]
            public ushort datano_e;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 200)]
            [FieldOffset(8)]
            public byte[] cdata;
        }
    }
}