using SupportDesk.Application.Service;
using SupportDesk.Domain.Service;

namespace SupportDesk.UnitTests.Customers;

/// <summary>Eligibility notes are shown to agents as written: readable contract types and dates, not enum names and ISO dates.</summary>
public sealed class EntitlementNoteTests
{
    [Theory]
    [InlineData(ContractType.Amc, "AMC")]
    [InlineData(ContractType.AnnualSupport, "Annual support")]
    [InlineData(ContractType.PrepaidHours, "Prepaid hours")]
    [InlineData(ContractType.TimeAndMaterial, "Time & material")]
    [InlineData(ContractType.Warranty, "Warranty")]
    [InlineData(ContractType.Subscription, "Subscription")]
    public void Contract_types_read_as_words(ContractType type, string expected) =>
        Assert.Equal(expected, EntitlementService.ContractTypeLabel(type));

    [Fact]
    public void Note_dates_use_day_month_name_and_year() =>
        Assert.Equal("14 Aug 2026", EntitlementService.Day(new DateOnly(2026, 8, 14)));
}
