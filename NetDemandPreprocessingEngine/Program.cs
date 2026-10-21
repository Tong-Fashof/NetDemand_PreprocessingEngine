using Microsoft.Data.SqlClient;
using NetDemandPreprocessingEngine;
using System;
using System.Data;

class Program
{

    // ผลลัพธ์การคำนวณ 1 แถว (1 เดือน ของ 1 สินค้า/คลัง)
    public record DemandResult(
        decimal AdjustedSalesQty,
        decimal OosUpliftQty,
        decimal NetDemandQty
    );

    // ======================================================
    // Pure In-Memory Calculation Engine (FM-01 + FM-02)
    // ไม่เชื่อมต่อ Database ใดๆ ทั้งสิ้น รับ input -> คืน output ตรงๆ
    // เพื่อให้ Unit Test เรียกใช้ได้ง่าย โดยไม่ต้องมี DB จริงมารองรับ
    // - rawSalesQty: ยอดขายดิบ (อาจเป็น null แทน NULL จาก DB, อาจติดลบได้)
    // - oosLostQty: ยอดเสียโอกาสจากของขาด (อาจเป็น null, ปกติไม่ควรติดลบแต่ต้องป้องกันไว้)
    // ======================================================
    public static DemandResult Calculate(decimal? rawSalesQty, decimal? oosLostQty)
    {
        // ---------- FM-01: Adjusted Sales ----------
        // ขั้นที่ 1 Null-Coalesce: NULL -> 0.0000
        decimal cleanRawSales = rawSalesQty ?? 0.0000m;

        // ขั้นที่ 2 Floor at Zero: ตัดยอดคืนสินค้าที่ทำให้ติดลบออก
        decimal adjustedSalesQty = Math.Max(0.0000m, cleanRawSales);


        // ---------- FM-02: Net True Demand ----------
        // ขั้นที่ 1 Null-Coalesce OOS: NULL -> 0.0000
        decimal cleanOosLost = oosLostQty ?? 0.0000m;

        // ขั้นที่ 2 Floor at Zero OOS: ป้องกันข้อมูล OOS ผิดปกติที่ติดลบ
        decimal oosUpliftQty = Math.Max(0.0000m, cleanOosLost);

        // ขั้นที่ 3 รวมยอดความต้องการแท้จริง
        decimal netDemandQty = adjustedSalesQty + oosUpliftQty;

        return new DemandResult(adjustedSalesQty, oosUpliftQty, netDemandQty);
    }


    // ======================================================
    // CONFIG:
    // - targetPeriod           = null  -> ใช้ค่า default (MAX period_year_month ล่าสุดใน DRP_STG_DRP_DEMAND_24M)
    // - targetWarehouseCode    = null  -> รันทุกคลัง (Full Refresh)
    // ======================================================


    static void Main(string[] args)
    {
        // ======================================================
        // ถ้า Argument --test ถูกส่งมา ให้รัน Unit Test แทนการทำงานจริง
        // ======================================================
        if (args.Contains("--test"))
        {
            Tests.RunUnitTests();
            return;
        }

        string? targetPeriod = null;          // เช่น "202609"   
        string? targetWarehouseCode = null;    // เช่น "CMPMH"
        try
        {
            // รับ Argument จาก Command Line (เช่น --warehouse CMPMH --period 202609)
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--warehouse" && i + 1 < args.Length)
                {
                    targetWarehouseCode = args[++i];
                }
                else if (args[i] == "--period" && i + 1 < args.Length)
                {
                    targetPeriod = args[++i];

                    // ตรวจรูปแบบ period (ต้องเป็น yyyyMM)
                    if (targetPeriod != null && !DateTime.TryParseExact(targetPeriod + "01", "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out _))
                    {
                        Console.WriteLine("--period ต้องเป็นรูปแบบ yyyyMM เช่น 202609");
                        return;
                    }
                }
                else
                {
                    Console.WriteLine($"ไม่รู้จัก Argument: {args[i]}");
                    Console.WriteLine($"Usage: NetDemandPreprocessingEngine.exe --warehouse CMPMH --period 202609");
                    return;
                }
            }


            // 1) ดึงข้อมูลดิบจาก DRP_STG_DRP_DEMAND_24M (พร้อมคำนวณ)
            DataTable data = GetRawData(targetPeriod, targetWarehouseCode);
            Console.WriteLine($"ดึงข้อมูลสำเร็จ: {data.Rows.Count} แถว");

            // 2) คำนวณ FM-01/FM-02 ด้วย DemandCalculator (ตัวเดียวกับที่ Unit Test ใช้)
            ApplyCalculations(data);

            // 3) เติมคอลัมน์ calculated_at (เวลาที่คำนวณ)
            AddCalculatedAtColumn(data);

            // 4) ล้างข้อมูลเก่า + บันทึกข้อมูลใหม่ (SqlBulk)
            SaveToTarget(data, targetWarehouseCode);

            Console.WriteLine("บันทึก Snapshot สำเร็จ");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"เกิดข้อผิดพลาด: {ex.Message}");
            throw;
        }
    }

    // ======================================================
    // Connect to Database
    // ======================================================
    private static SqlConnection ConnectData()
    {
        // TODO: ย้าย connection string เก็บใน environment
        string connectionString =
            "Server=192.168.2.117,9823;" +
            "Database=AxWebReport_UAT;" +
            "User ID=dev;" +
            "Password=P@ssw0rd22;" +
            "TrustServerCertificate=True;";

        SqlConnection connection = new SqlConnection(connectionString);

        try
        {
            connection.Open();
            Console.WriteLine("Connection successful!");
            return connection;
        }
        catch (Exception)
        {
            connection.Dispose();
            throw;
        }
    }


    // ======================================================
    // ดึงข้อมูล 24 เดือนจาก DRP_STG_DRP_DEMAND_24M
    // (FM-01=ปรับยอดขาย, FM-2.1-2.2=ปรับยอด OOS, FM-2.3=ยอดรวม(FM-01+FM-2.1-2.2), month_no)
    // คืนค่าเป็น DataTable เพื่อนำไป SqlBulkCopy ต่อได้เลย
    // ======================================================
    static DataTable GetRawData(string? targetPeriod, string? targetWarehouseCode)
    {
        // คำสั่ง SQL หลัก: ดึงข้อมูลดิบ + month_no เท่านั้น
        // *** คำนวณ FM-01/FM-02 ผ่าน DemandCalculator

        // - @pTargetPeriod / @pTargetWarehouseCode คือ parameter ที่รับจาก C# (ป้องกัน SQL Injection)
        // - ถ้า @TargetPeriod ไม่ถูกส่งมา ให้ใช้ MAX(period_year_month) จาก DRP_STG_DRP_DEMAND_24M แทน (ค่า default)
        // - @StartPeriod คำนวณย้อนหลัง 23 เดือนจาก @TargetPeriod (รวมกันครบ 24 เดือน = Sliding Window)
        // - month_no ใช้ ROW_NUMBER() ไล่ 1 ถึง 24 ต่อ (warehouse_code, item_id) ตามลำดับเวลา
        string query = @"
            -- รับค่า TargetPeriod: ถ้าไม่ระบุมา ใช้งวดล่าสุดที่มีใน Staging
            DECLARE @TargetPeriod VARCHAR(6) = @pTargetPeriod;
            IF @TargetPeriod IS NULL
                SELECT @TargetPeriod = MAX(period_year_month) FROM dbo.DRP_STG_DRP_DEMAND_24M;

            -- คำนวณ StartPeriod = TargetPeriod ย้อนหลัง 23 เดือน (รวม TargetPeriod = 24 เดือนพอดี)
            DECLARE @StartPeriod VARCHAR(6) =
                CONVERT(VARCHAR(6), DATEADD(MONTH, -23, CONVERT(DATE, @TargetPeriod + '01', 112)), 112);

            SELECT
                warehouse_code,
                item_id,
                period_year_month,

                -- ลำดับงวดสัมพัทธ์ 1 (เก่าสุด) ถึง 24 (ล่าสุด) ต่อ 1 สินค้า/คลัง
                ROW_NUMBER() OVER (
                    PARTITION BY warehouse_code, item_id
                    ORDER BY period_year_month ASC
                ) AS month_no,

                -- ส่งค่าดิบตรงๆ (อาจเป็น NULL หรือติดลบ) ให้ C# เป็นคนจัดการต่อ
                raw_sales_qty,
                oos_lost_qty

            FROM dbo.DRP_STG_DRP_DEMAND_24M
            WHERE period_year_month BETWEEN @StartPeriod AND @TargetPeriod
              AND (@pTargetWarehouseCode IS NULL OR warehouse_code = @pTargetWarehouseCode)
            ORDER BY warehouse_code, item_id, period_year_month;
        ";

        DataTable table = new DataTable();

        using SqlConnection connection = ConnectData();
        using SqlCommand command = new SqlCommand(query, connection);

        // ส่ง parameter (ป้องกัน SQL Injection) และรองรับค่า null
        command.Parameters.AddWithValue("@pTargetPeriod", (object?)targetPeriod ?? DBNull.Value);
        command.Parameters.AddWithValue("@pTargetWarehouseCode", (object?)targetWarehouseCode ?? DBNull.Value);

        using SqlDataReader reader = command.ExecuteReader();
        table.Load(reader); // โหลดผลลัพธ์ทั้งหมดเข้า DataTable โดยตรง (คอลัมน์ชื่อตรงกับ SELECT ด้านบน)

        return table;
    }


    // ======================================================
    // คำนวณ FM-01 (adjusted_sales_qty) และ FM-02 (oos_uplift_qty, net_demand_qty)
    // ด้วย DemandCalculator (Pure Function ตัวเดียวกับที่ Unit Test ใช้)
    // แล้วเติมผลลัพธ์กลับเข้า DataTable เป็นคอลัมน์ใหม่
    // ======================================================
    static void ApplyCalculations(DataTable table)
    {
        // เพิ่มคอลัมน์ผลลัพธ์ที่ตารางปลายทางต้องการ
        table.Columns.Add("adjusted_sales_qty", typeof(decimal));
        table.Columns.Add("oos_uplift_qty", typeof(decimal));
        table.Columns.Add("net_demand_qty", typeof(decimal));

        foreach (DataRow row in table.Rows)
        {
            // อ่านค่าดิบจาก DB (รองรับกรณีเป็น DBNull)
            decimal? rawSales = row["raw_sales_qty"] is DBNull ? null : Convert.ToDecimal(row["raw_sales_qty"]);
            decimal? oosLost = row["oos_lost_qty"] is DBNull ? null : Convert.ToDecimal(row["oos_lost_qty"]);

            // เรียก Pure Function ตัวเดียวกับที่ Tests.cs ใช้ทดสอบ
            DemandResult result = Calculate(rawSales, oosLost);

            // เติมผลลัพธ์กลับเข้าแถวเดิม
            row["adjusted_sales_qty"] = result.AdjustedSalesQty;
            row["oos_uplift_qty"] = result.OosUpliftQty;
            row["net_demand_qty"] = result.NetDemandQty;

            // raw_sales_qty ปลายทางต้อง Coalesce เป็น 0 (ไม่ Floor) ตาม Mapping Table
            row["raw_sales_qty"] = rawSales ?? 0.0000m;
        }
    }


    // ======================================================
    // เติมคอลัมน์ calculated_at (เวลาปัจจุบัน) ==> ใช้เป็น Audit Trail ว่าแถวนี้คำนวณตอนไหน
    // ======================================================
    static void AddCalculatedAtColumn(DataTable table)
    {
        table.Columns.Add("calculated_at", typeof(DateTime));
        DateTime now = DateTime.Now;
        foreach (DataRow row in table.Rows)
        {
            row["calculated_at"] = now;
        }
    }


    // ======================================================
    // บันทึกข้อมูลลงตารางปลายทาง: DRP_FCT_SKU_MONTHLY_DEMAND_PREP
    // ======================================================
    static void SaveToTarget(DataTable data, string? targetWarehouseCode)
    {
        /* 
         1) ลบข้อมูลเก่าก่อน(Idempotency Policy)
           - รันครบ 7 คลัง (targetWarehouseCode = null) -> TRUNCATE ทั้งตาราง
           - รันเฉพาะคลัง -> DELETE เฉพาะ warehouse_code นั้น
         2) Bulk Insert ข้อมูลใหม่
           - ทั้งหมดอยู่ใน Transaction เดียวกัน เพื่อกันข้อมูลหายถ้า Insert ล้มเหลวกลางทาง
        */

        // ชื่อตารางปลายทาง
        const string targetTable = "DRP_FCT_SKU_MONTHLY_DEMAND_PREP";

        using SqlConnection connection = ConnectData();
        using SqlTransaction transaction = connection.BeginTransaction();

        try
        {
            // --- ขั้นที่ 1: ลบข้อมูลเก่าตามเงื่อนไข Dual Idempotency Policy ---
            string deleteSql = targetWarehouseCode is null
                ? $"TRUNCATE TABLE {targetTable};"                                   // Full Refresh
                : $"DELETE FROM {targetTable} WHERE warehouse_code = @pWarehouse;";  // Scoped Delete

            using (SqlCommand deleteCommand = new SqlCommand(deleteSql, connection, transaction))
            {
                if (targetWarehouseCode is not null)
                    deleteCommand.Parameters.AddWithValue("@pWarehouse", targetWarehouseCode);

                deleteCommand.ExecuteNonQuery();
            }

            // --- ขั้นที่ 2: Bulk Insert ข้อมูลใหม่ทั้งหมดในครั้งเดียว ---
            using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction))
            {
                bulkCopy.DestinationTableName = targetTable;

                // จับคู่คอลัมน์ใน DataTable กับคอลัมน์ในตารางปลายทางให้ตรงชื่อ
                bulkCopy.ColumnMappings.Add("warehouse_code", "warehouse_code");
                bulkCopy.ColumnMappings.Add("item_id", "item_id");
                bulkCopy.ColumnMappings.Add("month_no", "month_no");
                bulkCopy.ColumnMappings.Add("period_year_month", "period_year_month");
                bulkCopy.ColumnMappings.Add("raw_sales_qty", "raw_sales_qty");
                bulkCopy.ColumnMappings.Add("adjusted_sales_qty", "adjusted_sales_qty");
                bulkCopy.ColumnMappings.Add("oos_uplift_qty", "oos_uplift_qty");
                bulkCopy.ColumnMappings.Add("net_demand_qty", "net_demand_qty");
                bulkCopy.ColumnMappings.Add("calculated_at", "calculated_at");

                // ทำการ Bulk Insert ข้อมูลทั้งหมดจาก DataTable ไปยังตารางปลายทาง
                bulkCopy.WriteToServer(data);
            }

            // ทุกอย่างสำเร็จ -> Commit ยืนยันธุรกรรม
            transaction.Commit();
        }
        catch
        {
            // เกิดข้อผิดพลาดระหว่างทาง -> Rollback ยกเลิกทุกอย่าง
            transaction.Rollback();
            throw;
        }
    }

}