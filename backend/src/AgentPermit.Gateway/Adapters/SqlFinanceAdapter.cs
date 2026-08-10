using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;

namespace AgentPermit.Gateway.Adapters;

/// <summary>
/// Executes governed Finance/Accounts-Payable actions against the real business database
/// (AgentPermitBusiness, schema <c>fin</c>). One adapter serves both the read target (FINANCE_READ)
/// and the transactional AP target (FINANCE_AP); the gateway has already decided ALLOW/approval before
/// anything here runs. This adapter holds the only downstream DB credential — agents never touch the DB.
/// </summary>
public sealed class SqlFinanceAdapter(string? connectionString, ILogger<SqlFinanceAdapter> logger) : ActionAdapterBase
{
    public const string Code = "SQL_FINANCE";
    public override string AdapterCode => Code;

    private bool Configured => !string.IsNullOrWhiteSpace(connectionString);

    private SqlConnection Open()
    {
        for (var attempt = 1; ; attempt++)
        {
            var c = new SqlConnection(connectionString);
            try { c.Open(); return c; }
            catch (SqlException) when (attempt < 5) { c.Dispose(); Thread.Sleep(3000 * attempt); } // serverless resume
        }
    }

    public override async Task<AdapterHealth> CheckHealthAsync(CancellationToken ct)
    {
        if (!Configured) return new AdapterHealth { Healthy = false, Message = "Business SQL connection not configured." };
        try { await using var c = Open(); _ = await c.ExecuteScalarAsync<int>("SELECT 1"); return new AdapterHealth { Healthy = true, Message = "OK" }; }
        catch (Exception ex) { return new AdapterHealth { Healthy = false, Message = ex.Message }; }
    }

    public override async Task<AdapterResult> ExecuteAsync(AdapterRequest request, CancellationToken ct)
    {
        if (!Configured) return AdapterResult.Fail("TARGET_NOT_CONFIGURED", "Business SQL connection is not configured.");
        var p = request.Parameters;
        try
        {
            await using var c = Open();
            return request.ActionId switch
            {
                "Vendor.Lookup"            => await VendorLookup(c, Str(p, "vendorCode")),
                "Invoice.Lookup"          => await InvoiceLookup(c, Str(p, "invoiceNumber")),
                "Invoice.Approve"         => await InvoiceApprove(c, Str(p, "invoiceNumber"), Str(p, "approver"), TryStr(p, "note")),
                "Payment.Issue"           => await PaymentIssue(c, request, Str(p, "invoiceNumber"), Str(p, "vendorCode"), Num(p, "amount"), Str(p, "currency")),
                "Vendor.UpdateBankDetails"=> await VendorUpdateBank(c, Str(p, "vendorCode"), Str(p, "newIban"), Str(p, "reason")),
                _ => AdapterResult.Fail("UNSUPPORTED_ACTION", $"SQL_FINANCE adapter does not handle '{request.ActionId}'.")
            };
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "SQL_FINANCE adapter failed for {ActionId} (execution {ExecutionId}).", request.ActionId, request.ExecutionId);
            return AdapterResult.Fail("TARGET_CALL_FAILED", ex.Message);
        }
    }

    // ---- operations ---------------------------------------------------------

    private static async Task<AdapterResult> VendorLookup(SqlConnection c, string vendorCode)
    {
        var row = await c.QueryFirstOrDefaultAsync(
            "SELECT VendorCode, Name, Country, Status, Iban FROM fin.Vendor WHERE VendorCode = @vendorCode",
            new { vendorCode });
        if (row is null) return AdapterResult.Fail("VENDOR_NOT_FOUND", $"No vendor '{vendorCode}'.");
        return AdapterResult.Ok(vendorCode, JsonSerializer.Serialize(new
        {
            vendorCode = (string)row.VendorCode, name = (string)row.Name, country = (string)row.Country,
            status = (string)row.Status, iban = Mask((string?)row.Iban) // masked in the result surface
        }));
    }

    private static async Task<AdapterResult> InvoiceLookup(SqlConnection c, string invoiceNumber)
    {
        var row = await c.QueryFirstOrDefaultAsync(
            @"SELECT i.InvoiceNumber, v.VendorCode, i.Amount, i.Currency, i.Status, i.DueDate
              FROM fin.Invoice i JOIN fin.Vendor v ON v.VendorId = i.VendorId
              WHERE i.InvoiceNumber = @invoiceNumber", new { invoiceNumber });
        if (row is null) return AdapterResult.Fail("INVOICE_NOT_FOUND", $"No invoice '{invoiceNumber}'.");
        return AdapterResult.Ok(invoiceNumber, JsonSerializer.Serialize(new
        {
            invoiceNumber = (string)row.InvoiceNumber, vendorCode = (string)row.VendorCode,
            amount = (decimal)row.Amount, currency = (string)row.Currency, status = (string)row.Status,
            dueDate = ((DateTime?)row.DueDate)?.ToString("yyyy-MM-dd")
        }));
    }

    private static async Task<AdapterResult> InvoiceApprove(SqlConnection c, string invoiceNumber, string approver, string? note)
    {
        var status = await c.QueryFirstOrDefaultAsync<string>(
            "SELECT Status FROM fin.Invoice WHERE InvoiceNumber = @invoiceNumber", new { invoiceNumber });
        if (status is null) return AdapterResult.Fail("INVOICE_NOT_FOUND", $"No invoice '{invoiceNumber}'.");
        if (!string.Equals(status, "Received", StringComparison.OrdinalIgnoreCase))
            return AdapterResult.Fail("INVOICE_NOT_APPROVABLE", $"Invoice '{invoiceNumber}' is '{status}', not 'Received'.");

        await c.ExecuteAsync(
            "UPDATE fin.Invoice SET Status = 'Approved', ApprovedBy = @approver, ApprovedUtc = SYSUTCDATETIME() WHERE InvoiceNumber = @invoiceNumber",
            new { invoiceNumber, approver });
        _ = note;
        return AdapterResult.Ok(invoiceNumber, JsonSerializer.Serialize(new { invoiceNumber, status = "Approved", approver }));
    }

    private static async Task<AdapterResult> PaymentIssue(SqlConnection c, AdapterRequest req, string invoiceNumber, string vendorCode, decimal amount, string currency)
    {
        var inv = await c.QueryFirstOrDefaultAsync(
            @"SELECT i.InvoiceId, i.VendorId, i.Amount, i.Status, v.VendorCode, v.Status AS VendorStatus
              FROM fin.Invoice i JOIN fin.Vendor v ON v.VendorId = i.VendorId
              WHERE i.InvoiceNumber = @invoiceNumber", new { invoiceNumber });
        if (inv is null) return AdapterResult.Fail("INVOICE_NOT_FOUND", $"No invoice '{invoiceNumber}'.");
        if (string.Equals((string)inv.Status, "Paid", StringComparison.OrdinalIgnoreCase))
            return AdapterResult.Fail("INVOICE_ALREADY_PAID", $"Invoice '{invoiceNumber}' is already paid.");
        if (!string.Equals((string)inv.VendorCode, vendorCode, StringComparison.OrdinalIgnoreCase))
            return AdapterResult.Fail("VENDOR_MISMATCH", $"Invoice '{invoiceNumber}' is not billed to vendor '{vendorCode}'.");
        if (string.Equals((string)inv.VendorStatus, "OnHold", StringComparison.OrdinalIgnoreCase))
            return AdapterResult.Fail("VENDOR_ON_HOLD", $"Vendor '{vendorCode}' is on hold; payment blocked by the target system.");

        var paymentRef = "PAY-" + req.ExecutionId.ToString("N")[..12].ToUpperInvariant();
        await c.ExecuteAsync(
            @"INSERT fin.Payment (PaymentRef, InvoiceId, VendorId, Amount, Currency, Status, IdempotencyKey, IssuedBy)
              VALUES (@paymentRef, @invoiceId, @vendorId, @amount, @currency, 'Issued', @idem, 'agentpermit-gateway');
              UPDATE fin.Invoice SET Status = 'Paid' WHERE InvoiceId = @invoiceId;",
            new { paymentRef, invoiceId = (int)inv.InvoiceId, vendorId = (int)inv.VendorId, amount, currency, idem = req.ActionRequestId.ToString() });
        return AdapterResult.Ok(paymentRef, JsonSerializer.Serialize(new { paymentRef, invoiceNumber, vendorCode, amount, currency, status = "Issued" }));
    }

    private static async Task<AdapterResult> VendorUpdateBank(SqlConnection c, string vendorCode, string newIban, string reason)
    {
        var v = await c.QueryFirstOrDefaultAsync(
            "SELECT VendorId, Iban FROM fin.Vendor WHERE VendorCode = @vendorCode", new { vendorCode });
        if (v is null) return AdapterResult.Fail("VENDOR_NOT_FOUND", $"No vendor '{vendorCode}'.");
        await c.ExecuteAsync(
            @"INSERT fin.VendorBankChange (VendorId, OldIban, NewIban, ChangedBy) VALUES (@vendorId, @oldIban, @newIban, 'agentpermit-gateway');
              UPDATE fin.Vendor SET Iban = @newIban WHERE VendorId = @vendorId;",
            new { vendorId = (int)v.VendorId, oldIban = (string?)v.Iban, newIban });
        _ = reason;
        return AdapterResult.Ok(vendorCode, JsonSerializer.Serialize(new { vendorCode, iban = Mask(newIban), status = "Updated" }));
    }

    // ---- helpers ------------------------------------------------------------

    private static string? Mask(string? iban) =>
        string.IsNullOrEmpty(iban) ? iban : iban.Length <= 4 ? "****" : new string('*', iban.Length - 4) + iban[^4..];
    private static string Str(JsonElement e, string n) => e.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
    private static string? TryStr(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static decimal Num(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDecimal() : 0m;
}
