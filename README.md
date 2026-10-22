# Net Demand Preprocessing Engine

C# Console App สำหรับคำนวณ **ยอดความต้องการขายที่แท้จริง (True Demand)** ของสินค้า (SKU) แต่ละชิ้นในแต่ละคลัง โดยดึงข้อมูลยอดขายดิบ 24 เดือนย้อนหลัง มาปรับยอดคืนสินค้าที่ติดลบให้เป็น 0 และรวมยอดเสียโอกาสจากสินค้าขาดสต็อก (OOS) แล้วบันทึกผลลัพธ์ลงฐานข้อมูลเป็น Snapshot ประจำงวด

ผลลัพธ์จากงานนี้จะถูกนำไปใช้ต่อใน **PBI-03 (Keep Gate 1)** เพื่อคัดกรอง SKU ที่ข้อมูลยังไม่พอสำหรับการพยากรณ์

## ทำอะไรบ้าง

1. ดึงข้อมูลยอดขายดิบ 24 เดือนล่าสุดจากตาราง Staging (`DRP_STG_DRP_DEMAND_24M`)
2. คำนวณ 2 สูตรหลัก:
   - **Adjusted Sales** — ปรับยอดขายที่ติดลบ (จากการคืนสินค้า) ให้เป็น 0
   - **Net True Demand** — รวมยอดขายที่ปรับแล้วกับยอดเสียโอกาสจากของขาดสต็อก (OOS Uplift)
3. บันทึกผลลัพธ์ลงตาราง `DRP_FCT_SKU_MONTHLY_DEMAND_PREP` แบบรันซ้ำได้ปลอดภัย 100%

## สูตรการคำนวณ

```
adjusted_sales_qty = Max(0, ยอดขายดิบ)          // ตัดยอดคืนสินค้าที่ทำให้ติดลบออก
oos_uplift_qty      = Max(0, ยอดเสียโอกาสจากของขาด)
net_demand_qty      = adjusted_sales_qty + oos_uplift_qty
```

ค่าที่เป็น `NULL` จากฐานข้อมูลจะถูกแทนที่ด้วย `0` ก่อนคำนวณเสมอ

## โครงสร้างโปรเจกต์

| ไฟล์ | หน้าที่ |
|---|---|
| `Program.cs` | จุดเริ่มโปรแกรม เชื่อมต่อฐานข้อมูล ดึงข้อมูล คำนวณ (`Calculate`) และบันทึกผล (SqlBulkCopy) |
| `Tests.cs` | Unit Test ตาม Test Case ที่ระบุใน Jira (TC-02-01 ถึง TC-02-12) |

## ข้อมูลที่ใช้

- `AxWebReport_UAT.dbo.DRP_STG_DRP_DEMAND_24M` ตาราง Staging เก็บยอดขายดิบ (`raw_sales_qty`) และยอดเสียโอกาส (`oos_lost_qty`) รายเดือน
- `AxWebReport_UAT.dbo.DRP_FCT_SKU_MONTHLY_DEMAND_PREP` ตารางปลายทางที่บันทึกผลลัพธ์ Snapshot รายงวด

## วิธีใช้งาน

```bash
# รันแบบปกติ: ประมวลผลครบทั้ง 7 คลัง ด้วยงวดล่าสุดที่มีในระบบ
NetDemandPreprocessingEngine.exe

# รันเจาะจงงวด + คลัง
NetDemandPreprocessingEngine.exe --period 202609 --warehouse CMPOS
```

| พารามิเตอร์ | บังคับ? | ความหมาย |
|---|---|---|
| `--period` | ไม่ | งวดเดือนเป้าหมาย (YYYYMM) ถ้าไม่ระบุ ใช้งวดล่าสุดที่มีในฐานข้อมูล |
| `--warehouse` | ไม่ | รหัสคลังที่ต้องการรันเฉพาะเจาะจง ถ้าไม่ระบุ รันครบทั้ง 7 คลัง |

## ⚠️ พฤติกรรมสำคัญที่ต้องรู้ก่อนรัน (Idempotency)

การรันซ้ำจะ **ลบข้อมูลเก่าก่อนบันทึกใหม่เสมอ** เพื่อไม่ให้เกิดข้อมูลซ้ำ แต่วิธีลบต่างกันตามเงื่อนไข:

- **ไม่ระบุคลัง** → ลบข้อมูล**ทั้งตาราง** (TRUNCATE) แล้วบันทึกใหม่ครบ 7 คลัง
- **ระบุคลัง** → ลบข้อมูลเฉพาะคลังนั้น (DELETE WHERE) แล้วบันทึกใหม่เฉพาะคลังนั้น

> 🔴 สำคัญ: ถ้ารันแบบระบุคลังด้วย `--period` ที่เก่ากว่าหรือ window แคบกว่าที่เคยรันไว้ จะทำให้ **ข้อมูล 24 เดือนของคลังนั้นถูกแทนที่ด้วยชุดที่เล็กกว่า** (ไม่ใช่การเพิ่มข้อมูล) ควรระบุ `--period` เป็นงวดล่าสุดเสมอเมื่อรันแบบเจาะจงคลัง

## ตัวอย่างผลลัพธ์เมื่อรันจริง (ต่อฐานข้อมูล)

```
Connection successful!
ดึงข้อมูลสำเร็จ: 282 แถว
Connection successful!
บันทึก Snapshot สำเร็จ
```

- **"ดึงข้อมูลสำเร็จ: N แถว"** — จำนวนแถวที่ดึงมาจาก Staging ตาม window 24 เดือน (คูณด้วยจำนวนคลังและ SKU ที่เข้าเงื่อนไข)
- **"บันทึก Snapshot สำเร็จ"** — ยืนยันว่าลบข้อมูลเก่า (ตาม TRUNCATE/DELETE) และบันทึกข้อมูลใหม่ลงตาราง `DRP_FCT_SKU_MONTHLY_DEMAND_PREP` เรียบร้อยภายใน Transaction เดียว ถ้าขั้นตอนไหนล้มเหลว ข้อมูลจะถูก Rollback ทั้งหมด ไม่ค้างครึ่งๆ กลางๆ

แต่ละแถวที่บันทึกจะมีฟิลด์: `warehouse_code`, `item_id`, `month_no` (1-24), `period_year_month`, `raw_sales_qty`, `adjusted_sales_qty`, `oos_uplift_qty`, `net_demand_qty`, `calculated_at`

## Unit Test (ไม่ต่อฐานข้อมูล)

Logic การคำนวณถูกแยกออกมาเป็น Pure Function (`Program.Calculate`) ที่ไม่พึ่งพาฐานข้อมูล ทำให้ทดสอบได้โดยไม่ต้องต่อ DB จริง

```bash
# รัน Unit Test (ไม่ต่อฐานข้อมูล)
dotnet run -- --test
```

ชุดทดสอบครอบคลุมกรณี: ยอดขายปกติ, ยอดคืนสินค้าติดลบ, OOS ติดลบผิดปกติ, ค่า NULL, ทศนิยม 4 ตำแหน่ง, และตัวเลขจำนวนมาก (ดูรายละเอียดใน `Tests.cs`)

### ตัวอย่างผลลัพธ์เมื่อรัน Test

```
===== Unit Test: DemandCalculator (FM-01 + FM-02) =====

[PASS] TC-02-01 - Normal Positive Case
   Input   : raw_sales_qty=50, oos_lost_qty=15
   Expected: adjusted=50, oos_uplift=15, net_demand=65
   Actual  : adjusted=50, oos_uplift=15, net_demand=65

[PASS] TC-02-04 - Negative Sales Return (Floor 0)
   Input   : raw_sales_qty=-15, oos_lost_qty=0
   Expected: adjusted=0, oos_uplift=0, net_demand=0
   Actual  : adjusted=0, oos_uplift=0, net_demand=0

...

=========================================
สรุปผล: PASS 12 / FAIL 0 (ทั้งหมด 12 เคส)
=========================================
```

แต่ละเคสแสดงทั้ง Input ที่ส่งเข้าไป, ค่า Expected (คำตอบที่ควรได้ตามสเปก) และค่า Actual (ผลจริงจากการรันฟังก์ชัน) ให้เทียบกันตรงๆ ถ้าแถวไหนขึ้น `FAIL` แปลว่าค่าคำนวณไม่ตรงสเปก ต้องตรวจสอบ Logic ใน `Program.Calculate` ก่อนนำไปใช้งานจริง
