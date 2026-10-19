
using System;
using Microsoft.Data.SqlClient;

class Program
{
    static void Main()
    {
        Get_rawData();
    }

    // Function สำหรับเชื่อมต่อ Database
    private static SqlConnection ConnectData()
    {
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
        catch (Exception ex)
        {
            connection.Dispose();
            throw;
        }
    }

    static void Get_rawData()
    {
        try
        {
            string query = @"
                SELECT
                  *,

                CASE
                    WHEN ISNULL(raw_sales_qty, 0) < 0 THEN
                    0 ELSE ISNULL(raw_sales_qty, 0) 
                  END AS adjusted_sales_qty,

                CASE
                    WHEN ISNULL(oos_lost_qty, 0) < 0 THEN
                    0 ELSE ISNULL(oos_lost_qty, 0) 
                  END AS oos_uplift_qty,

                CASE
                    WHEN ISNULL(raw_sales_qty, 0) < 0 THEN
                    0 ELSE ISNULL(raw_sales_qty, 0) 
                  END +
                CASE
                  WHEN ISNULL(oos_lost_qty, 0) < 0 THEN
                  0 ELSE ISNULL(oos_lost_qty, 0) 
                  END AS net_demand_qty

                FROM
                  dbo.DRP_STG_DRP_DEMAND_24M;
            ";

            using SqlConnection connection = ConnectData();             // เรียก Function เชื่อมต่อ Database
            using SqlCommand command = new SqlCommand(query, connection); // สร้างคำสั่ง SQL
            using SqlDataReader reader = command.ExecuteReader();       // อ่านข้อมูลจาก Database

            while (reader.Read())
            {
                string warehouse_code = reader["warehouse_code"]?.ToString() ?? "";
                string item_id = reader["item_id"]?.ToString() ?? "";
                string item_name = reader["item_name"]?.ToString() ?? "";
                string period_year_month = reader["period_year_month"]?.ToString() ?? "";
                decimal raw_sales_qty = Convert.ToDecimal(reader["raw_sales_qty"]);
                decimal oos_lost_qty = Convert.ToDecimal(reader["oos_lost_qty"]);
                string ingested_at = reader["ingested_at"]?.ToString() ?? "";

                decimal sales = Convert.ToDecimal(reader["adjusted_sales_qty"]);
                decimal oos = Convert.ToDecimal(reader["oos_uplift_qty"]);
                decimal netDemand = sales + oos;
            }

        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error retrieving data: {ex.Message}");
            throw;
        }
    }

}
