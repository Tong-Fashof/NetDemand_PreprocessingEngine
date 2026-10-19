using System;
using System.Data;
using Microsoft.Data.SqlClient;

class Program
{
    // ======================================================
    // CONFIG:
    // - targetPeriod           = null  -> ใช้ค่า default (MAX period_year_month ล่าสุดใน DRP_STG_DRP_DEMAND_24M)
    // - targetWarehouseCode    = null  -> รันทุกคลัง (Full Refresh)
    // ======================================================
    static void Main()
    {
        string? targetPeriod = null;          // เช่น "202609"
        string? targetWarehouseCode = null;    // เช่น "CMPMH"

        try
        {
            // 1) ดึงข้อมูลดิบจาก DRP_STG_DRP_DEMAND_24M (พร้อมคำนวณ)
            DataTable data = GetRawData(targetPeriod, targetWarehouseCode);
            Console.WriteLine($"ดึงข้อมูลสำเร็จ: {data.Rows.Count} แถว");

            // 2) เติมคอลัมน์ calculated_at (เวลาที่คำนวณ)
            AddCalculatedAtColumn(data);

            // 3) ล้างข้อมูลเก่า + บันทึกข้อมูลใหม่ (SqlBulk)
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
        // คำสั่ง SQL หลัก:
        // - @pTargetPeriod / @pTargetWarehouseCode คือ parameter ที่รับจาก C# (ป้องกัน SQL Injection)
        // - ถ้า @TargetPeriod ไม่ถูกส่งมา ให้ใช้ MAX(period_year_month) จาก DRP_STG_DRP_DEMAND_24M แทน (ค่า default)
        // - @StartPeriod คำนวณย้อนหลัง 23 เดือนจาก @TargetPeriod (รวมกันครบ 24 เดือน = Sliding Window)
        // - month_no ใช้ ROW_NUMBER() ไล่ 1 ถึง 24 ต่อ (warehouse_code, item_id) ตามลำดับเวลา

        string query = @"
            -- รับค่า TargetPeriod: ถ้าไม่ระบุมา ใช้งวดล่าสุดที่มีใน DRP_STG_DRP_DEMAND_24M
            DECLARE @TargetPeriod VARCHAR(6) = @pTargetPeriod;
            IF @TargetPeriod IS NULL SELECT @TargetPeriod = MAX(period_year_month) FROM dbo.DRP_STG_DRP_DEMAND_24M;

            -- คำนวณ StartPeriod = TargetPeriod ย้อนหลัง 23 เดือน (รวม TargetPeriod = 24 เดือนพอดี)
            -- หลักคิด: ย้อนหลัง N เดือน + เดือนตั้งต้นเอง = N+1 เดือน ดังนั้นย้อน 23 เดือน + ตัวเอง = 24 เดือน
            -- ถ้า TargetPeriod = 202608 (ส.ค. 2026) -> ย้อนหลัง 23 เดือน → StartPeriod = 202409 (ก.ย. 2024)
            DECLARE @StartPeriod VARCHAR(6) = CONVERT(VARCHAR(6), DATEADD(MONTH, -23, CONVERT(DATE, @TargetPeriod + '01', 112)), 112);

            SELECT
                *,
                -- ลำดับงวดสัมพัทธ์ 1 (เก่าสุด) ถึง 24 (ล่าสุด) ต่อ 1 สินค้า/คลัง
                ROW_NUMBER() OVER (
                    PARTITION BY warehouse_code, item_id
                    ORDER BY period_year_month ASC
                ) AS month_no,

                -- raw_sales_qty: เก็บค่า Coalesce เป็น 0 เท่านั้น ไม่ Floor (ตาม Mapping Table)
                ISNULL(raw_sales_qty, 0.0000) AS raw_sales_qty,

                -- FM-01: adjusted_sales_qty = Max(0, ISNULL(raw_sales_qty, 0))
                CASE WHEN ISNULL(raw_sales_qty, 0.0000) < 0 THEN 0.0000
                     ELSE ISNULL(raw_sales_qty, 0.0000) END AS adjusted_sales_qty,

                -- FM-02 (2.1-2.2): oos_uplift_qty = Max(0, ISNULL(oos_lost_qty, 0))
                CASE WHEN ISNULL(oos_lost_qty, 0.0000) < 0 THEN 0.0000
                     ELSE ISNULL(oos_lost_qty, 0.0000) END AS oos_uplift_qty,

                -- FM-02 (2.3): net_demand_qty = adjusted_sales_qty + oos_uplift_qty
                (CASE WHEN ISNULL(raw_sales_qty, 0.0000) < 0 THEN 0.0000
                      ELSE ISNULL(raw_sales_qty, 0.0000) END)
                +
                (CASE WHEN ISNULL(oos_lost_qty, 0.0000) < 0 THEN 0.0000
                      ELSE ISNULL(oos_lost_qty, 0.0000) END) AS net_demand_qty

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