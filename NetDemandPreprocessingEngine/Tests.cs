using System;
using System.Collections.Generic;
using System.Text;

namespace NetDemandPreprocessingEngine
{
    class Tests
    {

        // โครงสร้าง 1 เคสทดสอบ (ตรงตามตาราง TC-02-01 ถึง TC-02-11)
        private record TestCase(
            string Id,                      // ตำแหน่งที่ 1
            string Description,             // ตำแหน่งที่ 2
            decimal? RawSalesQty,           // ตำแหน่งที่ 3  <- Input
            decimal? OosLostQty,            // ตำแหน่งที่ 4  <- Input
            decimal ExpectedAdjustedSales,  // ตำแหน่งที่ 5  <- ผลที่ควรได้
            decimal ExpectedOosUplift,      // ตำแหน่งที่ 6  <- ผลที่ควรได้
            decimal ExpectedNetDemand       // ตำแหน่งที่ 7  <- ผลที่ควรได้
        );

        public static void RunUnitTests()
        {
            // ---------- รายการ Test Case (TC-02-01 ถึง TC-02-11) ----------
            List<TestCase> cases = new()
            {
                new("TC-02-01", "Normal Positive Case",              50.0000m,     15.0000m,     50.0000m,     15.0000m,     65.0000m),
                new("TC-02-02", "Normal Case Without OOS",            120.0000m,    0.0000m,      120.0000m,    0.0000m,      120.0000m),
                new("TC-02-03", "Zero Activity",                      0.0000m,      0.0000m,      0.0000m,      0.0000m,      0.0000m),
                new("TC-02-04", "Negative Sales Return (Floor 0)",    -15.0000m,    0.0000m,      0.0000m,      0.0000m,      0.0000m),
                new("TC-02-05", "Negative Sales with Positive OOS",   -20.0000m,    10.0000m,     0.0000m,      10.0000m,     10.0000m),
                new("TC-02-06", "Negative OOS Anomaly (Floor 0)",     45.0000m,     -5.0000m,     45.0000m,     0.0000m,      45.0000m),
                new("TC-02-07", "Both Negative Values",               -30.0000m,    -8.0000m,     0.0000m,      0.0000m,      0.0000m),
                new("TC-02-08", "Zero Sales with Full OOS Stockout",  0.0000m,      85.0000m,     0.0000m,      85.0000m,     85.0000m),
                new("TC-02-09", "Decimal Precision (4 Decimals)",     12.3456m,     7.8901m,      12.3456m,     7.8901m,      20.2357m),
                new("TC-02-10", "Small Non-Zero Decimal",             0.0001m,      0.0002m,      0.0001m,      0.0002m,      0.0003m),
                new("TC-02-11", "High-Volume / Large Number",         95000.5000m,  25000.2500m,  95000.5000m,  25000.2500m,  120000.7500m),
            };

            int passCount = 0;
            int failCount = 0;

            Console.WriteLine("===== Unit Test: DemandCalculator (FM-01 + FM-02) =====\n");

            foreach (TestCase tc in cases)
            {
                // เรียก Function ที่เขียนไว้ pass input เข้าไปตรงๆ (อยู่ใน class Program)
                Program.DemandResult result = Program.Calculate(tc.RawSalesQty, tc.OosLostQty);

                // เทียบผลลัพธ์ทีละฟิลด์
                bool isPass = result.AdjustedSalesQty == tc.ExpectedAdjustedSales &&
                    result.OosUpliftQty == tc.ExpectedOosUplift &&
                    result.NetDemandQty == tc.ExpectedNetDemand;

                if (isPass)
                {
                    passCount++;
                }
                else
                {
                    failCount++;
                }

                // แสดงผลลัพธ์เทียบ expected vs actual
                Console.WriteLine($"[{(isPass ? "PASS" : "FAIL")}] {tc.Id} - {tc.Description}");
                Console.WriteLine($"   Input   : raw_sales_qty={tc.RawSalesQty}, oos_lost_qty={tc.OosLostQty}");
                Console.WriteLine($"   Output  : adjusted={tc.ExpectedAdjustedSales}, oos_uplift={tc.ExpectedOosUplift}, net_demand={tc.ExpectedNetDemand}");
                Console.WriteLine($"   Actual  : adjusted={result.AdjustedSalesQty}, oos_uplift={result.OosUpliftQty}, net_demand={result.NetDemandQty}");
                Console.WriteLine();
            }

            Console.WriteLine("=========================================");
            Console.WriteLine($"สรุปผล: PASS {passCount} / FAIL {failCount} (ทั้งหมด {cases.Count} เคส)");
            Console.WriteLine("=========================================");
        }
    }
}
