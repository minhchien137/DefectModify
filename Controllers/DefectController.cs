using DefectModify.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Data.SqlClient;
using System.Text.Json;

namespace DefectModify.Controllers
{
    public class DefectController : Controller
    {
        private readonly ApplicationDbContext _context;
        private readonly string _connStr;
        private readonly IWebHostEnvironment _webHostEnvironment;

        public DefectController(ApplicationDbContext context, IConfiguration configuration, IWebHostEnvironment webHostEnvironment)
        {
            _context = context;
            _connStr = configuration.GetConnectionString("DefaultConnection")!;
            _webHostEnvironment = webHostEnvironment;
        }

        // ============================================================
        //  Đồng bộ SVN_Defect_Record_History -> SVN_Defect_Record
        //  Record là bảng TỔNG HỢP: Qty_NG = SUM(History.Qty_NG) theo nhóm
        //  (Item_code, Defect_Code, Operation, Work_Date, Shift), trong đó
        //  Work_Date/Shift được suy ra từ Time_line — CÙNG công thức với
        //  thủ tục SVN_InsertDefectReport (không dùng cột INSDatetime/
        //  Work_Date/Shift lưu sẵn trên History vì các cột đó không được
        //  thủ tục/Insert action set nhất quán).
        //  Day   : Time_line giờ > 08:00:00 và <= 20:00:00
        //  Night : phần còn lại; nếu giờ <= 08:00:00 thì Work_Date lùi 1 ngày
        // ============================================================
        private static (string WorkDateStr, string Shift) ComputeWorkDateShift(DateTime timeLine)
        {
            var gio = timeLine.TimeOfDay;
            var t0800 = new TimeSpan(8, 0, 0);
            var t2000 = new TimeSpan(20, 0, 0);

            string shift;
            DateTime workDate;

            if (gio > t0800 && gio <= t2000)
            {
                shift = "Day";
                workDate = timeLine.Date;
            }
            else
            {
                shift = "Night";
                workDate = gio <= t0800 ? timeLine.Date.AddDays(-1) : timeLine.Date;
            }

            return (workDate.ToString("yyyyMMdd"), shift);
        }

        // Tính lại tổng Qty_NG từ History cho đúng 1 nhóm (Item_code, Defect_Code,
        // Operation, Work_Date, Shift) rồi ghi đè xuống Record — update nếu còn dòng
        // History thuộc nhóm, xóa dòng Record nếu nhóm không còn dòng History nào
        // (SP gốc chỉ MERGE/insert cho tình huống thêm mới nên không cần xóa).
        private async Task SyncRecordGroupAsync(string? itemCode, string? defectCode, string? operation, string workDateStr, string shift)
        {
            if (string.IsNullOrEmpty(itemCode) || string.IsNullOrEmpty(defectCode) || string.IsNullOrEmpty(operation))
                return;

            var workDate = DateTime.ParseExact(workDateStr, "yyyyMMdd", System.Globalization.CultureInfo.InvariantCulture);

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();

            decimal? qtySum;
            await using (var sumCmd = new SqlCommand(@"
                SELECT SUM(CAST(Qty_NG AS DECIMAL))
                FROM dbo.SVN_Defect_Record_History
                WHERE Item_code = @itemCode AND Defect_Code = @defectCode AND Operation = @operation
                  AND Time_line IS NOT NULL
                  AND (CASE WHEN Time_line <= DATEADD(HOUR, 8, CAST(CAST(Time_line AS DATE) AS DATETIME))
                            THEN DATEADD(DAY, -1, CAST(Time_line AS DATE))
                            ELSE CAST(Time_line AS DATE) END) = @workDate
                  AND (CASE WHEN Time_line >  DATEADD(HOUR, 8,  CAST(CAST(Time_line AS DATE) AS DATETIME))
                             AND Time_line <= DATEADD(HOUR, 20, CAST(CAST(Time_line AS DATE) AS DATETIME))
                            THEN N'Day' ELSE N'Night' END) = @shift", conn))
            {
                sumCmd.Parameters.AddWithValue("@itemCode", itemCode);
                sumCmd.Parameters.AddWithValue("@defectCode", defectCode);
                sumCmd.Parameters.AddWithValue("@operation", operation);
                sumCmd.Parameters.AddWithValue("@workDate", workDate);
                sumCmd.Parameters.AddWithValue("@shift", shift);

                var result = await sumCmd.ExecuteScalarAsync();
                qtySum = (result == null || result == DBNull.Value) ? (decimal?)null : Convert.ToDecimal(result);
            }

            if (qtySum == null || qtySum == 0)
            {
                await using var delCmd = new SqlCommand(@"
                    DELETE FROM dbo.SVN_Defect_Record
                    WHERE Item_code = @itemCode AND Defect_Code = @defectCode
                      AND Operation = @operation AND INSDatetime = @workDateStr AND Shift = @shift", conn);
                delCmd.Parameters.AddWithValue("@itemCode", itemCode);
                delCmd.Parameters.AddWithValue("@defectCode", defectCode);
                delCmd.Parameters.AddWithValue("@operation", operation);
                delCmd.Parameters.AddWithValue("@workDateStr", workDateStr);
                delCmd.Parameters.AddWithValue("@shift", shift);
                await delCmd.ExecuteNonQueryAsync();
            }
            else
            {
                await using var mergeCmd = new SqlCommand(@"
                    MERGE INTO dbo.SVN_Defect_Record AS target
                    USING (SELECT @itemCode AS Item_code, @defectCode AS Defect_Code,
                                  @operation AS Operation, @qtyNG AS Qty_NG) AS source
                        ON target.Item_code = source.Item_code
                       AND target.Defect_Code = source.Defect_Code
                       AND target.Operation = source.Operation
                       AND target.INSDatetime = @workDateStr
                       AND target.Shift = @shift
                    WHEN MATCHED THEN
                        UPDATE SET target.Qty_NG = source.Qty_NG
                    WHEN NOT MATCHED THEN
                        INSERT (Item_code, Defect_Code, Qty_NG, INSDatetime, Operation, Shift)
                        VALUES (source.Item_code, source.Defect_Code, source.Qty_NG, @workDateStr, source.Operation, @shift);", conn);
                mergeCmd.Parameters.AddWithValue("@itemCode", itemCode);
                mergeCmd.Parameters.AddWithValue("@defectCode", defectCode);
                mergeCmd.Parameters.AddWithValue("@operation", operation);
                mergeCmd.Parameters.AddWithValue("@qtyNG", qtySum.Value);
                mergeCmd.Parameters.AddWithValue("@workDateStr", workDateStr);
                mergeCmd.Parameters.AddWithValue("@shift", shift);
                await mergeCmd.ExecuteNonQueryAsync();
            }
        }

        // ============================================================
        //  GET: /Defect/Modify
        // ============================================================
        public async Task<IActionResult> Modify(
            string? dateFrom, string? dateTo,
            string? itemCode, string? operation,
            string? table = "history")
        {
            ViewBag.DateFrom    = dateFrom;
            ViewBag.DateTo      = dateTo;
            ViewBag.ItemCode    = itemCode;
            ViewBag.Operation   = operation;
            ViewBag.ActiveTable = table ?? "history";

            // ---- Helper: convert "yyyy-MM-dd" → "yyyyMMdd" for string comparison ----
            string? fromStr = dateFrom?.Replace("-", "");
            string? toStr   = dateTo?.Replace("-", "");

            // ---- SVN_Defect_Record_History ----
            var histQ = _context.SVN_Defect_Record_Histories.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(fromStr))
                histQ = histQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(fromStr) >= 0);
            if (!string.IsNullOrEmpty(toStr))
                histQ = histQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(toStr) <= 0);
            if (!string.IsNullOrEmpty(itemCode))
                histQ = histQ.Where(r => r.Item_code != null && r.Item_code.Contains(itemCode));
            if (!string.IsNullOrEmpty(operation))
                histQ = histQ.Where(r => r.Operation != null && r.Operation.Contains(operation));

            // ---- SVN_Defect_Record ----
            // Bảng này không có khóa chính thật trong DB, dùng AsNoTracking để tránh
            // EF Core ném lỗi "duplicate key" khi có nhiều dòng trùng
            // (Item_code, Defect_Code, INSDatetime, Operation) nhưng khác Shift.
            var recQ = _context.SVN_Defect_Records.AsNoTracking().AsQueryable();

            if (!string.IsNullOrEmpty(fromStr))
                recQ = recQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(fromStr) >= 0);
            if (!string.IsNullOrEmpty(toStr))
                recQ = recQ.Where(r => r.INSDatetime != null && r.INSDatetime.CompareTo(toStr) <= 0);
            if (!string.IsNullOrEmpty(itemCode))
                recQ = recQ.Where(r => r.Item_code != null && r.Item_code.Contains(itemCode));
            if (!string.IsNullOrEmpty(operation))
                recQ = recQ.Where(r => r.Operation != null && r.Operation.Contains(operation));

            var vm = new DefectModifyViewModel
            {
                HistoryRecords = await histQ.OrderByDescending(r => r.INSDatetime).Take(300).ToListAsync(),
                DefectRecords  = await recQ.OrderByDescending(r => r.INSDatetime).Take(300).ToListAsync()
            };

            // Dropdown Operations cho modal Insert
            ViewBag.Operations = await _context.SVN_quality_reasons
                .Where(r => r.operation != null)
                .Select(r => r.operation)
                .Distinct()
                .OrderBy(o => o)
                .ToListAsync();

            return View(vm);
        }

        // ============================================================
        //  POST: Edit SVN_Defect_Record_History
        //  History và Record là 2 bảng tách biệt, không tự đồng bộ.
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditHistory(SVN_Defect_Record_History model,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "history",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };

            var existing = await _context.SVN_Defect_Record_Histories.FindAsync(model.Id);
            if (existing == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // Lưu lại khóa nhóm TRƯỚC khi sửa để sau khi lưu có thể tính lại
            // đúng nhóm Record cũ (nếu bản ghi bị "chuyển nhóm" do đổi
            // Item_code/Defect_Code/Operation)
            var oldItemCode   = existing.Item_code;
            var oldDefectCode = existing.Defect_Code;
            var oldOperation  = existing.Operation;
            var oldTimeLine   = existing.Time_line;

            // Capture old values for audit log
            string oldValues = JsonSerializer.Serialize(new
            {
                existing.Work_order, existing.Item_code, existing.Defect_Code,
                existing.Defect_Name, existing.Qty_NG, existing.INSDatetime,
                existing.Work_Date, existing.Shift,
                existing.Operation, existing.Employer_code, existing.Employer_name, existing.Note
            });

            // Apply changes
            existing.Work_order    = model.Work_order;
            existing.Item_code     = model.Item_code;
            existing.Defect_Code   = model.Defect_Code;
            existing.Defect_Name   = model.Defect_Name;
            existing.Qty_NG        = model.Qty_NG;
            existing.INSDatetime   = model.INSDatetime;
            existing.Work_Date     = model.Work_Date;
            existing.Shift         = model.Shift;
            existing.Operation     = model.Operation;
            existing.Employer_code = model.Employer_code;
            existing.Employer_name = model.Employer_name;
            existing.Note          = model.Note;
            // Time_line giữ nguyên, không ghi đè

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record_History",
                Action     = "Edit",
                RecordId   = model.Id.ToString(),
                OldValues  = oldValues,
                NewValues  = JsonSerializer.Serialize(new
                {
                    model.Work_order, model.Item_code, model.Defect_Code,
                    model.Defect_Name, model.Qty_NG, model.INSDatetime,
                    model.Work_Date, model.Shift,
                    model.Operation, model.Employer_code, model.Employer_name, model.Note
                }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            try { await _context.SaveChangesAsync(); }
            catch (Exception ex)
            {
                TempData["Error"] = $"Lỗi khi lưu: {ex.InnerException?.Message ?? ex.Message}";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // ---- Đồng bộ sang SVN_Defect_Record ----
            try
            {
                bool groupChanged = existing.Item_code != oldItemCode
                                  || existing.Defect_Code != oldDefectCode
                                  || existing.Operation != oldOperation
                                  || existing.Time_line != oldTimeLine;

                if (oldTimeLine.HasValue)
                {
                    var (oldWorkDateStr, oldShift) = ComputeWorkDateShift(oldTimeLine.Value);
                    await SyncRecordGroupAsync(oldItemCode, oldDefectCode, oldOperation, oldWorkDateStr, oldShift);
                }

                if (groupChanged && existing.Time_line.HasValue)
                {
                    var (newWorkDateStr, newShift) = ComputeWorkDateShift(existing.Time_line.Value);
                    await SyncRecordGroupAsync(existing.Item_code, existing.Defect_Code, existing.Operation, newWorkDateStr, newShift);
                }
            }
            catch (Exception ex)
            {
                TempData["Error"] = $"Đã cập nhật History nhưng đồng bộ Record thất bại: {ex.InnerException?.Message ?? ex.Message}";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            TempData["Success"] = $"Đã cập nhật bản ghi #{model.Id} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Insert SVN_Defect_Record_History
        //  - Thêm mới 1 dòng vào History (kèm upload ảnh nếu có)
        //  - History và Record là 2 bảng tách biệt, không tự đồng bộ.
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> InsertHistory(SVN_Defect_Record_History model,
            IFormFile? imageFile,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "history",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };

            // ---- Xử lý upload ảnh ----
            if (imageFile != null && imageFile.Length > 0)
            {
                var allowedExtensions = new[] { ".jpg", ".jpeg", ".png", ".gif", ".bmp", ".webp" };
                var ext = Path.GetExtension(imageFile.FileName).ToLower();

                if (!allowedExtensions.Contains(ext))
                {
                    TempData["Error"] = "Chỉ cho phép upload ảnh: jpg, jpeg, png, gif, bmp, webp";
                    return RedirectToAction(nameof(Modify), redirectParams);
                }

                if (imageFile.Length > 5 * 1024 * 1024)
                {
                    TempData["Error"] = "Kích thước ảnh không được vượt quá 5MB";
                    return RedirectToAction(nameof(Modify), redirectParams);
                }

                var uploadsFolder = Path.Combine(_webHostEnvironment.WebRootPath, "uploads", "defect-images");
                if (!Directory.Exists(uploadsFolder))
                    Directory.CreateDirectory(uploadsFolder);

                var fileName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid():N}{ext}";
                var filePath = Path.Combine(uploadsFolder, fileName);

                using (var stream = new FileStream(filePath, FileMode.Create))
                    await imageFile.CopyToAsync(stream);

                model.Image_error = $"/uploads/defect-images/{fileName}";
            }

            // ---- Gán Time_line từ INSDatetime (parse YYYYMMDD) + giờ random ----
            if (!string.IsNullOrEmpty(model.INSDatetime)
                && DateTime.TryParseExact(model.INSDatetime, "yyyyMMdd",
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var parsedDate))
            {
                var rng = new Random();
                model.Time_line = parsedDate
                    .AddHours(rng.Next(0, 24))
                    .AddMinutes(rng.Next(0, 60))
                    .AddSeconds(rng.Next(0, 60));
            }
            else
            {
                model.Time_line = DateTime.Now;
            }

            _context.SVN_Defect_Record_Histories.Add(model);

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record_History",
                Action     = "Insert",
                RecordId   = null,
                NewValues  = JsonSerializer.Serialize(new
                {
                    model.Work_order, model.Item_code, model.Defect_Code,
                    model.Defect_Name, model.Qty_NG, model.INSDatetime,
                    model.Work_Date, model.Shift,
                    model.Operation, model.Employer_code, model.Employer_name,
                    model.Note, model.Image_error
                }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            try { await _context.SaveChangesAsync(); }
            catch (Exception ex)
            {
                TempData["Error"] = $"Lỗi khi lưu History: {ex.InnerException?.Message ?? ex.Message}";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // ---- Đồng bộ sang SVN_Defect_Record ----
            if (model.Time_line.HasValue)
            {
                try
                {
                    var (workDateStr, shift) = ComputeWorkDateShift(model.Time_line.Value);
                    await SyncRecordGroupAsync(model.Item_code, model.Defect_Code, model.Operation, workDateStr, shift);
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Đã thêm History nhưng đồng bộ Record thất bại: {ex.InnerException?.Message ?? ex.Message}";
                    return RedirectToAction(nameof(Modify), redirectParams);
                }
            }

            TempData["Success"] = "Đã thêm lịch sử lỗi thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record_History
        //  History và Record là 2 bảng tách biệt, không tự đồng bộ.
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteHistory(int id,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "history",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };

            var record = await _context.SVN_Defect_Record_Histories.FindAsync(id);
            if (record == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // Lưu lại khóa nhóm trước khi xóa để tính lại Record sau khi History đã mất dòng này
            var delItemCode   = record.Item_code;
            var delDefectCode = record.Defect_Code;
            var delOperation  = record.Operation;
            var delTimeLine   = record.Time_line;

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record_History",
                Action     = "Delete",
                RecordId   = id.ToString(),
                OldValues  = JsonSerializer.Serialize(record),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            _context.SVN_Defect_Record_Histories.Remove(record);
            await _context.SaveChangesAsync();

            // ---- Đồng bộ sang SVN_Defect_Record ----
            if (delTimeLine.HasValue)
            {
                try
                {
                    var (workDateStr, shift) = ComputeWorkDateShift(delTimeLine.Value);
                    await SyncRecordGroupAsync(delItemCode, delDefectCode, delOperation, workDateStr, shift);
                }
                catch (Exception ex)
                {
                    TempData["Error"] = $"Đã xóa History nhưng đồng bộ Record thất bại: {ex.InnerException?.Message ?? ex.Message}";
                    return RedirectToAction(nameof(Modify), redirectParams);
                }
            }

            TempData["Success"] = $"Đã xóa bản ghi #{id} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Edit SVN_Defect_Record  (no single PK → raw SQL)
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> EditRecord(
            SVN_Defect_Record model,
            string origItemCode, string origDefectCode,
            string origInsDatetime, string origOperation, string? origShift,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "record",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };
            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record",
                Action     = "Edit",
                RecordId   = $"{origItemCode}|{origDefectCode}|{origInsDatetime}|{origOperation}|{origShift}",
                OldValues  = JsonSerializer.Serialize(new { origItemCode, origDefectCode, origInsDatetime, origOperation, origShift }),
                NewValues  = JsonSerializer.Serialize(new { model.Qty_NG, model.Employer_code, model.Employer_name, model.Shift }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            // Shift nằm trong WHERE (theo giá trị GỐC origShift) để chỉ update
            // đúng 1 dòng — tránh update nhầm dòng khác ca (VD: Day vs Night)
            // có cùng Item_code/Defect_Code/INSDatetime/Operation.
            await _context.Database.ExecuteSqlRawAsync(
                @"UPDATE SVN_Defect_Record
                  SET Qty_NG = {0}, Employer_code = {1}, Employer_name = {2}, Shift = {3}
                  WHERE Item_code = {4} AND Defect_Code = {5}
                    AND INSDatetime = {6} AND Operation = {7}
                    AND ((Shift = {8}) OR (Shift IS NULL AND {8} IS NULL))",
                model.Qty_NG, model.Employer_code, model.Employer_name, model.Shift,
                origItemCode, origDefectCode, origInsDatetime, origOperation, origShift);

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã cập nhật bản ghi {origItemCode} / {origDefectCode} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Delete SVN_Defect_Record
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteRecord(
            string itemCode, string defectCode,
            string insDatetime, string operation, string? shift,
            string? filterDateFrom, string? filterDateTo,
            string? filterItemCode, string? filterOperation)
        {
            var redirectParams = new {
                table     = "record",
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                itemCode  = filterItemCode,
                operation = filterOperation
            };
            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Defect_Record",
                Action     = "Delete",
                RecordId   = $"{itemCode}|{defectCode}|{insDatetime}|{operation}|{shift}",
                OldValues  = JsonSerializer.Serialize(new { itemCode, defectCode, insDatetime, operation, shift }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            // Shift nằm trong WHERE để chỉ xóa đúng 1 dòng — tránh xóa nhầm
            // dòng khác ca có cùng Item_code/Defect_Code/INSDatetime/Operation.
            await _context.Database.ExecuteSqlRawAsync(
                @"DELETE FROM SVN_Defect_Record
                  WHERE Item_code = {0} AND Defect_Code = {1}
                    AND INSDatetime = {2} AND Operation = {3}
                    AND ((Shift = {4}) OR (Shift IS NULL AND {4} IS NULL))",
                itemCode, defectCode, insDatetime, operation, shift);

            await _context.SaveChangesAsync();
            TempData["Success"] = $"Đã xóa bản ghi {itemCode} / {defectCode} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  GET: /Defect/Log  — ADO.NET thuần, không qua EF
        // ============================================================
        public async Task<IActionResult> Log(
            string? action, string? tableName,
            string? dateFrom, string? dateTo)
        {
            ViewBag.Action    = action;
            ViewBag.TableName = tableName;
            ViewBag.DateFrom  = dateFrom;
            ViewBag.DateTo    = dateTo;

            var logs = new List<DefectEditLog>();

            var wheres = new List<string>();
            if (!string.IsNullOrEmpty(action))     wheres.Add("Action = @action");
            if (!string.IsNullOrEmpty(tableName))  wheres.Add("TableName = @tableName");
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out _))
                wheres.Add("ModifiedAt >= @dateFrom");
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out _))
                wheres.Add("ModifiedAt < @dateTo");

            string whereClause = wheres.Any() ? "WHERE " + string.Join(" AND ", wheres) : "";
            string sql = $"SELECT TOP 500 Id, TableName, Action, RecordId, OldValues, NewValues, ModifiedAt, ModifiedBy FROM DefectEditLog {whereClause} ORDER BY ModifiedAt DESC";

            await using var conn = new SqlConnection(_connStr);
            await conn.OpenAsync();
            await using var cmd = new SqlCommand(sql, conn);

            if (!string.IsNullOrEmpty(action))    cmd.Parameters.AddWithValue("@action",    action);
            if (!string.IsNullOrEmpty(tableName)) cmd.Parameters.AddWithValue("@tableName", tableName);
            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
                cmd.Parameters.AddWithValue("@dateFrom", df);
            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
                cmd.Parameters.AddWithValue("@dateTo", dt.AddDays(1));

            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                logs.Add(new DefectEditLog
                {
                    Id         = reader.GetInt32(0),
                    TableName  = reader.IsDBNull(1) ? null : reader.GetString(1),
                    Action     = reader.IsDBNull(2) ? null : reader.GetString(2),
                    RecordId   = reader.IsDBNull(3) ? null : reader.GetString(3),
                    OldValues  = reader.IsDBNull(4) ? null : reader.GetString(4),
                    NewValues  = reader.IsDBNull(5) ? null : reader.GetString(5),
                    ModifiedAt = reader.GetDateTime(6),
                    ModifiedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
                });
            }

            return View(logs);
        }

        // ============================================================
        //  Proxy → DefectManagement Odoo API
        //  Tránh CORS khi gọi từ browser sang port khác
        // ============================================================
        private const string _odooBase = "http://10.10.99.10:8108";

        [HttpGet]
        public async Task<IActionResult> OdooSearchName([FromQuery] string nameEmployer)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url = $"{_odooBase}/api/Odoo/SearchName?nameEmployer={Uri.EscapeDataString(nameEmployer)}";
                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                return Content(body, "application/json");
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> OdooSearch([FromQuery] string productionCode)
        {
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                var url = $"{_odooBase}/api/Odoo/search?productionCode={Uri.EscapeDataString(productionCode)}";
                var resp = await http.GetAsync(url);
                var body = await resp.Content.ReadAsStringAsync();
                return Content(body, "application/json");
            }
            catch (Exception ex)
            {
                return Json(new { error = ex.Message });
            }
        }

        // ============================================================
        //  API: Lấy danh sách Defect Code + Name theo Operation
        //  Dùng cho dropdown Mã lỗi trong modal Insert
        // ============================================================
        [HttpGet]
        public async Task<IActionResult> GetDefectCodesByOperation([FromQuery] string operation)
        {
            var codes = await _context.SVN_quality_reasons
                .Where(r => r.operation == operation)
                .OrderBy(r => r.code)
                .Select(r => new { code = r.code, name = r.name })
                .ToListAsync();
            return Json(codes);
        }
    }
}