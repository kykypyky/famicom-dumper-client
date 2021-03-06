﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Cluster.Famicom
{
    public static class CoolboyWriter
    {
        public static void WriteWithGPIO(FamicomDumperConnection dumper, string fileName)
        {
            byte[] PRG;
            try
            {
                var nesFile = new NesFile(fileName);
                PRG = nesFile.PRG;
            }
            catch
            {
                var nesFile = new UnifFile(fileName);
                PRG = nesFile.Fields["PRG0"];
            }
            while (PRG.Length < 512 * 1024)
            {
                var PRGbig = new byte[PRG.Length * 2];
                Array.Copy(PRG, 0, PRGbig, 0, PRG.Length);
                Array.Copy(PRG, 0, PRGbig, PRG.Length, PRG.Length);
                PRG = PRGbig;
            }

            int prgBanks = PRG.Length / 0x2000;

            Console.Write("Reset... ");
            dumper.Reset();
            Console.WriteLine("OK");
            dumper.WriteCpu(0xA001, 0x00); // RAM protect
            var writeStartTime = DateTime.Now;
            var lastSectorTime = DateTime.Now;
            var timeTotal = new TimeSpan();
            for (int bank = 0; bank < prgBanks; bank += 2)
            {
                int outbank = bank / 16;
                byte r0 = (byte)((outbank & 0x07) | ((outbank & 0xc0) >> 2));
                byte r1 = (byte)(((outbank & 0x30) >> 2) | ((outbank << 1) & 0x10));
                byte r2 = 0;
                byte r3 = 0;
                dumper.WriteCpu(0x6000, new byte[] { r0 });
                dumper.WriteCpu(0x6001, new byte[] { r1 });
                dumper.WriteCpu(0x6002, new byte[] { r2 });
                dumper.WriteCpu(0x6003, new byte[] { r3 });

                int inbank = bank % 64;
                dumper.WriteCpu(0x8000, new byte[] { 6, (byte)(inbank) });
                dumper.WriteCpu(0x8000, new byte[] { 7, (byte)(inbank | 1) });

                var data = new byte[0x4000];
                int pos = bank * 0x2000;
                if (pos % (128 * 1024) == 0)
                {
                    timeTotal = new TimeSpan((DateTime.Now - lastSectorTime).Ticks * (prgBanks - bank) / 16);
                    timeTotal = timeTotal.Add(DateTime.Now - writeStartTime);
                    lastSectorTime = DateTime.Now;
                    Console.Write("Erasing sector... ");
                    dumper.ErasePrgFlash(FamicomDumperConnection.FlashAccessType.CoolboyGPIO);
                    Console.WriteLine("OK");
                }
                Array.Copy(PRG, pos, data, 0, data.Length);
                var timePassed = DateTime.Now - writeStartTime;
                Console.Write("Writing {0}/{1} ({2}%, {3:D2}:{4:D2}:{5:D2}/{6:D2}:{7:D2}:{8:D2})... ", bank / 2 + 1, prgBanks / 2, (int)(100 * bank / prgBanks),
                    timePassed.Hours, timePassed.Minutes, timePassed.Seconds, timeTotal.Hours, timeTotal.Minutes, timeTotal.Seconds);
                dumper.WritePrgFlash(0x0000, data, FamicomDumperConnection.FlashAccessType.CoolboyGPIO, true);
                Console.WriteLine("OK");
            }
        }

        public static void GetInfo(FamicomDumperConnection dumper)
        {
            Console.Write("Reset... ");
            dumper.Reset();
            Console.WriteLine("OK");
            int bank = 0;
            byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                | (1 << 6)); // resets 4th mask bit
            byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                | (((bank >> 6) & 1) << 4) // 6
                | (1 << 7)); // resets 5th mask bit
            byte r2 = 0;
            byte r3 = (byte)((1 << 4) // NROM mode
                | ((bank & 7) << 1)); // 2, 1, 0 bits
            dumper.WriteCpu(0x6000, new byte[] { r0 });
            dumper.WriteCpu(0x6001, new byte[] { r1 });
            dumper.WriteCpu(0x6002, new byte[] { r2 });
            dumper.WriteCpu(0x6003, new byte[] { r3 });
            CommonHelper.GetFlashSize(dumper);
        }

        public static void Write(FamicomDumperConnection dumper, string fileName, IEnumerable<int> badSectors, bool silent, bool needCheck = false, bool writePBBs = false)
        {
            byte[] PRG;
            if (Path.GetExtension(fileName).ToLower() == ".bin")
            {
                PRG = File.ReadAllBytes(fileName);
            }
            else
            {
                try
                {
                    var nesFile = new NesFile(fileName);
                    PRG = nesFile.PRG;
                }
                catch
                {
                    var nesFile = new UnifFile(fileName);
                    PRG = nesFile.Fields["PRG0"];
                }
            }

            int prgBanks = PRG.Length / 0x4000;

            Console.Write("Reset... ");
            dumper.Reset();
            Console.WriteLine("OK");
            int flashSize = CommonHelper.GetFlashSize(dumper);
            if (PRG.Length > flashSize)
                throw new Exception("This ROM is too big for this cartridge");
            PPBErase(dumper);

            var writeStartTime = DateTime.Now;
            var lastSectorTime = DateTime.Now;
            var timeTotal = new TimeSpan();
            int errorCount = 0;
            for (int bank = 0; bank < prgBanks; bank++)
            {
                if (badSectors.Contains(bank / 8)) bank += 8; // bad sector :(
                try
                {
                    byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                        | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                        | (1 << 6)); // resets 4th mask bit
                    byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                        | (((bank >> 6) & 1) << 4) // 6
                        | (1 << 7)); // resets 5th mask bit
                    byte r2 = 0;
                    byte r3 = (byte)((1 << 4) // NROM mode
                        | ((bank & 7) << 1)); // 2, 1, 0 bits
                    dumper.WriteCpu(0x6000, new byte[] { r0 });
                    dumper.WriteCpu(0x6001, new byte[] { r1 });
                    dumper.WriteCpu(0x6002, new byte[] { r2 });
                    dumper.WriteCpu(0x6003, new byte[] { r3 });

                    var data = new byte[0x4000];
                    int pos = bank * 0x4000;
                    if (pos % (128 * 1024) == 0)
                    {
                        timeTotal = new TimeSpan((DateTime.Now - lastSectorTime).Ticks * (prgBanks - bank) / 8);
                        timeTotal = timeTotal.Add(DateTime.Now - writeStartTime);
                        lastSectorTime = DateTime.Now;
                        Console.Write("Erasing sector... ");
                        dumper.ErasePrgFlash(FamicomDumperConnection.FlashAccessType.Direct);
                        Console.WriteLine("OK");
                    }
                    Array.Copy(PRG, pos, data, 0, data.Length);
                    var timePassed = DateTime.Now - writeStartTime;
                    Console.Write("Writing {0}/{1} ({2}%, {3:D2}:{4:D2}:{5:D2}/{6:D2}:{7:D2}:{8:D2})... ", bank + 1, prgBanks, (int)(100 * bank / prgBanks),
                        timePassed.Hours, timePassed.Minutes, timePassed.Seconds, timeTotal.Hours, timeTotal.Minutes, timeTotal.Seconds);
                    dumper.WritePrgFlash(0x0000, data, FamicomDumperConnection.FlashAccessType.Direct, true);
                    Console.WriteLine("OK");
                    if ((bank % 8 == 7) || (bank == prgBanks - 1))
                        PPBWrite(dumper, (uint)bank / 8);
                }
                catch (Exception ex)
                {
                    errorCount++;
                    if (errorCount >= 3)
                        throw ex;
                    if (!silent) Program.errorSound.PlaySync();
                    Console.WriteLine("Error: " + ex.Message);
                    bank = (bank & ~7) - 1;
                    Console.WriteLine("Lets try again");
                    Console.Write("Reset... ");
                    dumper.Reset();
                    Console.WriteLine("OK");
                    continue;
                }
            }
            if (errorCount > 0)
                Console.WriteLine("Warning! Error count: {0}", errorCount);

            if (needCheck)
            {
                Console.WriteLine("Starting check process");
                Console.Write("Reset... ");
                dumper.Reset();
                Console.WriteLine("OK");

                var readStartTime = DateTime.Now;
                lastSectorTime = DateTime.Now;
                timeTotal = new TimeSpan();
                for (int bank = 0; bank < prgBanks; bank++)
                {
                    byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                        | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                        | (1 << 6)); // resets 4th mask bit
                    byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                        | (((bank >> 6) & 1) << 4) // 6
                        | (1 << 7)); // resets 5th mask bit
                    byte r2 = 0;
                    byte r3 = (byte)((1 << 4) // NROM mode
                        | ((bank & 7) << 1)); // 2, 1, 0 bits
                    dumper.WriteCpu(0x6000, new byte[] { r0 });
                    dumper.WriteCpu(0x6001, new byte[] { r1 });
                    dumper.WriteCpu(0x6002, new byte[] { r2 });
                    dumper.WriteCpu(0x6003, new byte[] { r3 });

                    var data = new byte[0x4000];
                    int pos = bank * 0x4000;
                    if (pos % (128 * 1024) == 0)
                    {
                        timeTotal = new TimeSpan((DateTime.Now - lastSectorTime).Ticks * (prgBanks - bank) / 8);
                        timeTotal = timeTotal.Add(DateTime.Now - readStartTime);
                        lastSectorTime = DateTime.Now;
                    }
                    Array.Copy(PRG, pos, data, 0, data.Length);
                    UInt16 crc = 0;
                    foreach (var a in data)
                    {
                        crc ^= a;
                        for (int i = 0; i < 8; ++i)
                        {
                            if ((crc & 1) != 0)
                                crc = (UInt16)((crc >> 1) ^ 0xA001);
                            else
                                crc = (UInt16)(crc >> 1);
                        }
                    }
                    var timePassed = DateTime.Now - readStartTime;
                    Console.Write("Reading CRC {0}/{1} ({2}%, {3:D2}:{4:D2}:{5:D2}/{6:D2}:{7:D2}:{8:D2})... ", bank + 1, prgBanks, (int)(100 * bank / prgBanks),
                        timePassed.Hours, timePassed.Minutes, timePassed.Seconds, timeTotal.Hours, timeTotal.Minutes, timeTotal.Seconds);
                    var crcr = dumper.ReadCpuCrc(0x8000, 0x4000);
                    if (crcr != crc)
                        throw new Exception(string.Format("Check failed: {0:X4} != {1:X4}", crcr, crc));
                    else
                        Console.WriteLine("OK (CRC = {0:X4})", crcr);
                }
                if (errorCount > 0)
                {
                    Console.WriteLine("Warning! Error count: {0}", errorCount);
                    return;
                }
            }
        }

        public static byte PPBRead(FamicomDumperConnection dumper, uint sector)
        {
            // Select sector
            int bank = (int)(sector * 8);
            byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                | (1 << 6)); // resets 4th mask bit
            byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                | (((bank >> 6) & 1) << 4) // 6
                | (1 << 7)); // resets 5th mask bit
            byte r2 = 0;
            byte r3 = (byte)((1 << 4) // NROM mode
                | ((bank & 7) << 1)); // 2, 1, 0 bits
            dumper.WriteCpu(0x6000, new byte[] { r0 });
            dumper.WriteCpu(0x6001, new byte[] { r1 });
            dumper.WriteCpu(0x6002, new byte[] { r2 });
            dumper.WriteCpu(0x6003, new byte[] { r3 });
            // PPB Command Set Entry
            dumper.WriteCpu(0x8AAA, 0xAA);
            dumper.WriteCpu(0x8555, 0x55);
            dumper.WriteCpu(0x8AAA, 0xC0);
            // PPB Status Read
            var result = dumper.ReadCpu(0x8000, 1)[0];
            // PPB Command Set Exit
            dumper.WriteCpu(0x8000, 0x90);
            dumper.WriteCpu(0x8000, 0x00);
            return result;
        }

        public static void PPBWrite(FamicomDumperConnection dumper, uint sector)
        {
            Console.Write($"Writing PPB for sector #{sector}... ");
            // Select sector
            int bank = (int)(sector * 8);
            byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                | (1 << 6)); // resets 4th mask bit
            byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                | (((bank >> 6) & 1) << 4) // 6
                | (1 << 7)); // resets 5th mask bit
            byte r2 = 0;
            byte r3 = (byte)((1 << 4) // NROM mode
                | ((bank & 7) << 1)); // 2, 1, 0 bits
            dumper.WriteCpu(0x6000, new byte[] { r0 });
            dumper.WriteCpu(0x6001, new byte[] { r1 });
            dumper.WriteCpu(0x6002, new byte[] { r2 });
            dumper.WriteCpu(0x6003, new byte[] { r3 });
            // PPB Command Set Entry
            dumper.WriteCpu(0x8AAA, 0xAA);
            dumper.WriteCpu(0x8555, 0x55);
            dumper.WriteCpu(0x8AAA, 0xC0);
            // PPB Program
            dumper.WriteCpu(0x8000, 0xA0);
            dumper.WriteCpu(0x8000, 0x00);
            // Sector 0
            dumper.WriteCpu(0x5000, 0);
            dumper.WriteCpu(0x5001, 0);
            // Check
            while (true)
            {
                var b0 = dumper.ReadCpu(0x8000, 1)[0];
                //dumper.ReadCpu(0x0000, 1);
                var b1 = dumper.ReadCpu(0x8000, 1)[0];
                var tg = b0 ^ b1;
                if ((tg & (1 << 6)) == 0) // DQ6 = not toggle
                {
                    Thread.Sleep(1);
                    break;
                }
                else// DQ6 = toggle
                {
                    if ((b0 & (1 << 5)) != 0) // DQ5 = 1
                    {
                        b0 = dumper.ReadCpu(0x8000, 1)[0];
                        //dumper.ReadCpu(0x0000, 1);
                        b1 = dumper.ReadCpu(0x8000, 1)[0];
                        tg = b0 ^ b1;
                        if ((tg & (1 << 6)) == 0) // DQ6 = not toggle
                            break;
                        else
                            throw new Exception("PPB write failed");
                    }
                }
            }
            var r = dumper.ReadCpu(0x8000, 1)[0];
            if ((r & 1) != 0) // DQ0 = 1
                throw new Exception("PPB write failed");
            // PPB Command Set Exit
            dumper.WriteCpu(0x8000, 0x90);
            dumper.WriteCpu(0x8000, 0x00);
            Console.WriteLine("OK");
        }

        public static void PPBErase(FamicomDumperConnection dumper)
        {
            Console.Write($"Erasing all PBBs... ");
            // Sector 0
            int bank = 0;
            byte r0 = (byte)(((bank >> 3) & 0x07) // 5, 4, 3 bits
                | (((bank >> 9) & 0x03) << 4) // 10, 9 bits
                | (1 << 6)); // resets 4th mask bit
            byte r1 = (byte)((((bank >> 7) & 0x03) << 2) // 8, 7
                | (((bank >> 6) & 1) << 4) // 6
                | (1 << 7)); // resets 5th mask bit
            byte r2 = 0;
            byte r3 = (byte)((1 << 4) // NROM mode
                | ((bank & 7) << 1)); // 2, 1, 0 bits
            dumper.WriteCpu(0x6000, new byte[] { r0 });
            dumper.WriteCpu(0x6001, new byte[] { r1 });
            dumper.WriteCpu(0x6002, new byte[] { r2 });
            dumper.WriteCpu(0x6003, new byte[] { r3 });
            // PPB Command Set Entry
            dumper.WriteCpu(0x8AAA, 0xAA);
            dumper.WriteCpu(0x8555, 0x55);
            dumper.WriteCpu(0x8AAA, 0xC0);
            // All PPB Erase
            dumper.WriteCpu(0x8000, 0x80);
            dumper.WriteCpu(0x8000, 0x30);
            // Check
            while (true)
            {
                var b0 = dumper.ReadCpu(0x8000, 1)[0];
                var b1 = dumper.ReadCpu(0x8000, 1)[0];
                var tg = b0 ^ b1;
                if ((tg & (1 << 6)) == 0) // DQ6 = not toggle
                {
                    Thread.Sleep(1);
                    break;
                }
                else// DQ6 = toggle
                {
                    if ((b0 & (1 << 5)) != 0) // DQ5 = 1
                    {
                        b0 = dumper.ReadCpu(0x8000, 1)[0];
                        b1 = dumper.ReadCpu(0x8000, 1)[0];
                        tg = b0 ^ b1;
                        if ((tg & (1 << 6)) == 0) // DQ6 = not toggle
                            break;
                        else
                            throw new Exception("PPB erase failed");
                    }
                }
            }
            var r = dumper.ReadCpu(0x8000, 1)[0];
            if ((r & 1) != 1) // DQ0 = 0
                throw new Exception("PPB erase failed");
            // PPB Command Set Exit
            dumper.WriteCpu(0x8000, 0x90);
            dumper.WriteCpu(0x8000, 0x00);
            Console.WriteLine("OK");
        }
    }
}
