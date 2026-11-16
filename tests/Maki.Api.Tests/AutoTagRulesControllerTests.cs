using Maki.Api.Controllers;
using Maki.Api.Dtos;
using Maki.Core.Entities;
using Maki.Core.Tags;
using Microsoft.AspNetCore.Mvc;

namespace Maki.Api.Tests;

public sealed class AutoTagRulesControllerTests : IDisposable
{
    private readonly TestDb _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task A_save_without_groups_changes_the_first_group_and_keeps_the_others()
    {
        int tagId, ruleId;
        using (var db = _db.NewContext())
        {
            var tag = new Tag { Label = "Webtoons" };
            db.Tags.Add(tag);
            db.SaveChanges();
            var rule = new AutoTagRule
            {
                TagId = tag.Id,
                ConditionsJson = ConditionGroups.Serialize(
                [
                    [new AutoTagCondition(AutoTagFields.Type, "manga")],
                    [new AutoTagCondition(AutoTagFields.Genre, "Ecchi")],
                ]),
            };
            db.AutoTagRules.Add(rule);
            db.SaveChanges();
            (tagId, ruleId) = (tag.Id, rule.Id);
        }

        using (var db = _db.NewContext())
        {
            var result = await new AutoTagRulesController(new TestLocalizer(), db, null!).Update(ruleId,
                new SaveAutoTagRuleRequest(tagId, false, [new AutoTagConditionDto(AutoTagFields.Type, "manhwa")]),
                CancellationToken.None);
            Assert.IsType<OkObjectResult>(result);
        }

        using var check = _db.NewContext();
        var groups = ConditionGroups.Parse(check.AutoTagRules.Single(r => r.Id == ruleId).ConditionsJson);
        Assert.Equal(2, groups.Count);
        Assert.Equal("manhwa", Assert.Single(groups[0]).Value);
        Assert.Equal("Ecchi", Assert.Single(groups[1]).Value);
    }
}
