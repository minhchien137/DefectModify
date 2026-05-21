using DefectModify.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace DefectModify.Controllers
{
    public class DowntimeController : Controller
    {
        private readonly ApplicationDbContext _context;

        public DowntimeController(ApplicationDbContext context)
        {
            _context = context;
        }


        public async Task<IActionResult> Modify(
            string? dateFrom, string? dateTo,
            string? code, string? state,
            string? operation, string? issCode)
        {
            ViewBag.DateFrom = dateFrom;
            ViewBag.DateTo = dateTo;
            ViewBag.Code = code;
            ViewBag.State = state;
            ViewBag.Operation = operation;
            ViewBag.IssCode = issCode;

            var q = _context.SVN_Downtime_Infos.AsQueryable();

            if (!string.IsNullOrEmpty(dateFrom) && DateTime.TryParse(dateFrom, out var df))
                q = q.Where(r => r.Datetime != null && r.Datetime >= df);

            if (!string.IsNullOrEmpty(dateTo) && DateTime.TryParse(dateTo, out var dt))
                q = q.Where(r => r.Datetime != null && r.Datetime <= dt.AddDays(1).AddSeconds(-1));

            if (!string.IsNullOrEmpty(code))
                q = q.Where(r => r.Code != null && r.Code.Contains(code));

            if (!string.IsNullOrEmpty(state))
                q = q.Where(r => r.State != null && r.State == state);

            if (!string.IsNullOrEmpty(operation))
                q = q.Where(r => r.Operation != null && r.Operation.Contains(operation));

            if (!string.IsNullOrEmpty(issCode))
                q = q.Where(r => r.ISS_Code != null && r.ISS_Code.Contains(issCode));

            // Lấy danh sách State distinct để đổ vào dropdown
            var stateList = await _context.SVN_Downtime_Infos
                .Where(r => r.State != null)
                .Select(r => r.State!)
                .Distinct()
                .OrderBy(s => s)
                .ToListAsync();
            ViewBag.StateList = stateList;

            var records = await q
                .OrderByDescending(r => r.Datetime)
                .Take(300)
                .ToListAsync();

            return View(records);
        }

        // ============================================================
        //  POST: Edit SVN_Downtime_Info
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(SVN_Downtime_Info model,
            string? filterDateFrom, string? filterDateTo,
            string? filterCode, string? filterState,
            string? filterOperation, string? filterIssCode)
        {
            var redirectParams = new
            {
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                code      = filterCode,
                state     = filterState,
                operation = filterOperation,
                issCode   = filterIssCode
            };

            var existing = await _context.SVN_Downtime_Infos.FindAsync(model.Id);
            if (existing == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            // Audit log vào DefectEditLog (tái dùng bảng log sẵn có)
            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Downtime_Info",
                Action     = "Edit",
                RecordId   = model.Id.ToString(),
                OldValues  = JsonSerializer.Serialize(new
                {
                    existing.Code, existing.Name, existing.State,
                    existing.Operation, existing.EstimateTime,
                    existing.Description, existing.Datetime,
                    existing.ISS_Code, existing.SVNCode
                }),
                NewValues  = JsonSerializer.Serialize(new
                {
                    model.Code, model.Name, model.State,
                    model.Operation, model.EstimateTime,
                    model.Description, model.Datetime,
                    model.ISS_Code, model.SVNCode
                }),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            // Apply changes (Image giữ nguyên, không ghi đè)
            existing.Code         = model.Code;
            existing.Name         = model.Name;
            existing.State        = model.State;
            existing.Operation    = model.Operation;
            existing.EstimateTime = model.EstimateTime;
            existing.Description  = model.Description;
            existing.Datetime     = model.Datetime;
            existing.ISS_Code     = model.ISS_Code;
            existing.SVNCode      = model.SVNCode;

            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã cập nhật bản ghi #{model.Id} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }

        // ============================================================
        //  POST: Delete SVN_Downtime_Info
        // ============================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Delete(int id,
            string? filterDateFrom, string? filterDateTo,
            string? filterCode, string? filterState,
            string? filterOperation, string? filterIssCode)
        {
            var redirectParams = new
            {
                dateFrom  = filterDateFrom,
                dateTo    = filterDateTo,
                code      = filterCode,
                state     = filterState,
                operation = filterOperation,
                issCode   = filterIssCode
            };

            var record = await _context.SVN_Downtime_Infos.FindAsync(id);
            if (record == null)
            {
                TempData["Error"] = "Không tìm thấy bản ghi!";
                return RedirectToAction(nameof(Modify), redirectParams);
            }

            _context.DefectEditLogs.Add(new DefectEditLog
            {
                TableName  = "SVN_Downtime_Info",
                Action     = "Delete",
                RecordId   = id.ToString(),
                OldValues  = JsonSerializer.Serialize(record),
                ModifiedAt = DateTime.Now,
                ModifiedBy = User.Identity?.Name ?? "anonymous"
            });

            _context.SVN_Downtime_Infos.Remove(record);
            await _context.SaveChangesAsync();

            TempData["Success"] = $"Đã xóa bản ghi #{id} thành công!";
            return RedirectToAction(nameof(Modify), redirectParams);
        }
    }
}