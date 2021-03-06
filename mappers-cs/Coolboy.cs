﻿using System;
using System.Collections.Generic;

namespace Cluster.Famicom.Mappers
{
    public class Coolboy : IMapper
    {
        public string Name
        {
            get { return "Coolboy"; }
        }

        public int Number
        {
            get { return -1; }
        }

        public string UnifName
        {
            get { return "COOLBOY"; }
        }

        public int DefaultPrgSize
        {
            get { return 1024 * 1024 * 32; }
        }

        public int DefaultChrSize
        {
            get { return 0; }
        }

        public void DumpPrg(FamicomDumperConnection dumper, List<byte> data, int size)
        {
            dumper.Reset();
            int banks = size / 0x4000;

            for (int bank = 0; bank < banks; bank++)
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

                Console.Write("Reading PRG banks #{0}/{1}... ", bank, banks);
                data.AddRange(dumper.ReadCpu(0x8000, 0x4000));
                Console.WriteLine("OK");
            }
        }

        public void DumpChr(FamicomDumperConnection dumper, List<byte> data, int size)
        {
            return;
        }

        public void EnablePrgRam(FamicomDumperConnection dumper)
        {
            dumper.Reset();
            dumper.WriteCpu(0xA001, 0x00);
            dumper.WriteCpu(0x6003, 0x80);
            dumper.WriteCpu(0xA001, 0x80);
        }
    }
}
