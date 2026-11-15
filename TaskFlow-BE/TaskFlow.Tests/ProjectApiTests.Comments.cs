using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Entities;
using TaskFlow.Tests.Fixtures;
namespace TaskFlow.Tests;
public sealed partial class ProjectApiTests
{
    private static string CommentsPath(Companies c) => W(c.A) + "/tasks/" + c.TaskA.Id + "/comments";
    [MySqlFact]
    public async Task CommentsCrudKeepsMetadataHistoryClearsDeletedTextAndNotifiesOnlyNewMentions()
    {
        await using var host = new ProjectHost(fixture, true, true, true, false, true, true); await host.InitializeAsync(); var c = await SeedCompanies(host); await GrantNotificationRecipient(host,c);
        using var owner = await host.LoginAsync(0); using var recipient = await host.LoginAsync(2); var path = CommentsPath(c);
        var body = "<script>PRIVATE_COMMENT_CANARY</script> @[" + host.Users[2].Id + "] @[" + host.Users[0].Id + "]";
        var created = await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new { body })); var id = created.GetProperty("id").GetString(); Assert.Equal(body,created.GetProperty("body").GetString()); Assert.Equal("1",created.GetProperty("version").GetString());
        Assert.Equal(2, created.GetProperty("mentions").GetArrayLength());
        var notifications = await Body(await recipient.GetAsync(W(c.A)+"/notifications")); Assert.Equal("TaskMention",notifications.GetProperty("items")[0].GetProperty("kind").GetString());
        Assert.Equal(0,(await Body(await owner.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        var edited = await Body(await host.MemberWriteAsync(owner,HttpMethod.Put,path+"/"+id,new { body = body+" edited", expectedVersion="1" })); Assert.Equal("2",edited.GetProperty("version").GetString());
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Put,path+"/"+id,new { body = body+" edited", expectedVersion="2" }));
        Assert.Equal(1,(await Body(await recipient.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        await Problem(await host.MemberWriteAsync(recipient,HttpMethod.Put,path+"/"+id,new { body="cannot edit",expectedVersion="2" }),HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(recipient,HttpMethod.Delete,path+"/"+id,new {expectedVersion="2"}),HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Delete,path+"/"+id,new {expectedVersion="2"})).StatusCode);
        var item = (await Body(await recipient.GetAsync(path))).GetProperty("items")[0]; Assert.Equal(JsonValueKind.Null,item.GetProperty("body").ValueKind); Assert.NotEqual(JsonValueKind.Null,item.GetProperty("deletedAtUtc").ValueKind);
        var history = await Body(await recipient.GetAsync(path+"/"+id+"/history")); Assert.Equal(3,history.GetProperty("items").GetArrayLength()); Assert.DoesNotContain("PRIVATE_COMMENT_CANARY",history.ToString());
        Assert.Equal(0,(await Body(await recipient.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        await using var db=fixture.CreateContext(); var row=await db.TaskComments.SingleAsync(x=>x.Id==long.Parse(id!)); Assert.Equal("",row.Body); Assert.Equal(3,row.Version);
        Assert.All(await db.OutboxEvents.Where(e=>e.WorkspaceId==c.A.Id).ToArrayAsync(),e=>Assert.DoesNotContain("PRIVATE_COMMENT_CANARY",JsonSerializer.Serialize(e)));
    }
    [MySqlFact]
    public async Task CommentsScopePrecedesValidationAndMentionsCannotEnumerateForeignOrRevokedPeople()
    {
        await using var host = new ProjectHost(fixture,true,true,true,false,true,true); await host.InitializeAsync(); var c=await SeedCompanies(host); await GrantNotificationRecipient(host,c);
        using var owner=await host.LoginAsync(0); using var recipient=await host.LoginAsync(2); using var outsider=await host.LoginAsync(1); var path=CommentsPath(c);
        await Problem(await outsider.GetAsync(path+"?page=invalid"),HttpStatusCode.NotFound);
        await Problem(await owner.GetAsync(W(c.B)+"/tasks/"+c.TaskA.Id+"/comments/mention-candidates?search=x"),HttpStatusCode.NotFound);
        await Problem(await host.MemberWriteAsync(outsider,HttpMethod.Post,path,new {body=""}),HttpStatusCode.NotFound);
        foreach(var target in new[]{host.Users[1].Id,long.MaxValue})
            Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new {body="@["+target+"]"})).StatusCode);
        var candidates=await Body(await owner.GetAsync(path+"/mention-candidates?search="+Uri.EscapeDataString(host.Users[2].DisplayName)));
        Assert.Single(candidates.GetProperty("items").EnumerateArray()); Assert.DoesNotContain("email",candidates.ToString(),StringComparison.OrdinalIgnoreCase);
        long otherTask; await using(var db=fixture.CreateContext()) otherTask=await db.Tasks.Where(t=>t.ProjectId==c.A2.Id).Select(t=>t.Id).SingleAsync();
        var otherPath=W(c.A)+"/tasks/"+otherTask+"/comments";
        Assert.Equal(0,(await Body(await owner.GetAsync(otherPath+"/mention-candidates?search="+Uri.EscapeDataString(host.Users[2].DisplayName)))).GetProperty("items").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Post,otherPath,new{body="@["+host.Users[2].Id+"]"})).StatusCode);
        await using(var db=fixture.CreateContext()) await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.RevokedAtUtc,DateTime.UtcNow));
        Assert.Equal(0,(await Body(await owner.GetAsync(path+"/mention-candidates?search="+Uri.EscapeDataString(host.Users[2].DisplayName)))).GetProperty("items").GetArrayLength());
        await Problem(await recipient.GetAsync(path),HttpStatusCode.NotFound);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new {body="@["+host.Users[2].Id+"]"})).StatusCode);
    }
    [MySqlFact]
    public async Task CommentsConflictReadOnlyRolesAndOwnerModerationRespectPermissions()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true); await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);
        using var owner=await host.LoginAsync(0);using var author=await host.LoginAsync(2);using var second=await host.LoginAsync(2);var path=CommentsPath(c);
        var guestPath=W(c.B)+"/tasks/"+c.TaskB.Id+"/comments";
        Assert.False((await Body(await owner.GetAsync(guestPath))).GetProperty("canComment").GetBoolean());
        await Problem(await host.MemberWriteAsync(owner,HttpMethod.Post,guestPath,new{body="Guest denied"}),HttpStatusCode.Forbidden);
        var created=await Body(await host.MemberWriteAsync(author,HttpMethod.Post,path,new{body="Original"}));var id=created.GetProperty("id").GetString();
        var changes=await Task.WhenAll(host.MemberWriteAsync(author,HttpMethod.Put,path+"/"+id,new{body="First",expectedVersion="1"}),host.MemberWriteAsync(second,HttpMethod.Put,path+"/"+id,new{body="Second",expectedVersion="1"}));
        Assert.Single(changes,r=>r.IsSuccessStatusCode);var conflict=Assert.Single(changes,r=>r.StatusCode==HttpStatusCode.Conflict);Assert.Equal("resource.conflict",(await Problem(conflict,HttpStatusCode.Conflict)).GetProperty("code").GetString());
        await using(var db=fixture.CreateContext())await db.ProjectMembers.IgnoreQueryFilters().Where(m=>m.ProjectId==c.A1.Id&&m.UserId==host.Users[2].Id).ExecuteUpdateAsync(s=>s.SetProperty(m=>m.Role,MembershipRole.Viewer));
        Assert.False((await Body(await author.GetAsync(path))).GetProperty("canComment").GetBoolean());
        await Problem(await host.MemberWriteAsync(author,HttpMethod.Post,path,new{body="Viewer denied"}),HttpStatusCode.Forbidden);
        await Problem(await host.MemberWriteAsync(owner,HttpMethod.Put,path+"/"+id,new{body="Owner cannot rewrite",expectedVersion="2"}),HttpStatusCode.Forbidden);
        Assert.Equal(HttpStatusCode.NoContent,(await host.MemberWriteAsync(owner,HttpMethod.Delete,path+"/"+id,new{expectedVersion="2"})).StatusCode);
    }
    [MySqlFact]
    public async Task CommentsInputsCsrfDefaultsAndTransactionFailureAreSafe()
    {
        await using(var disabled=new ProjectHost(fixture,true,true,true,false,true)){await disabled.InitializeAsync();var c=await SeedCompanies(disabled);using var user=await disabled.LoginAsync(0);await Problem(await user.GetAsync(CommentsPath(c)),HttpStatusCode.NotFound);}
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true,new FailOutbox());await host.InitializeAsync();var company=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var path=CommentsPath(company);
        Assert.Equal(HttpStatusCode.BadRequest,(await owner.PostAsJsonAsync(path,new{body="No CSRF"})).StatusCode);
        foreach(var body in new[]{" ",new string('x',5001),"bad\u0001control"}) Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{body})).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{body="Spoof",authorId="1"})).StatusCode);
        await Problem(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{body="Rollback private comment"}),HttpStatusCode.InternalServerError);
        await using var db=fixture.CreateContext();Assert.False(await db.TaskComments.AnyAsync(c=>c.WorkspaceId==company.A.Id));Assert.False(await db.CommentRevisions.AnyAsync(c=>c.WorkspaceId==company.A.Id));Assert.False(await db.OutboxEvents.AnyAsync(c=>c.WorkspaceId==company.A.Id));
    }
    [MySqlFact]
    public async Task CommentsMutedRecipientAndDeletedTaskNeverProduceVisiblePrivateMentions()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);await GrantNotificationRecipient(host,c);using var owner=await host.LoginAsync(0);using var recipient=await host.LoginAsync(2);var path=CommentsPath(c);
        await using(var db=fixture.CreateContext()){db.NotificationPreferences.Add(new(){WorkspaceId=c.A.Id,UserId=host.Users[2].Id,InAppEnabled=false});await db.SaveChangesAsync();}
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{body="Muted @["+host.Users[2].Id+"]"}));Assert.Equal(0,(await Body(await recipient.GetAsync(W(c.A)+"/notifications"))).GetProperty("totalCount").GetInt32());
        await using(var db=fixture.CreateContext())await db.Tasks.IgnoreQueryFilters().Where(t=>t.Id==c.TaskA.Id).ExecuteUpdateAsync(s=>s.SetProperty(t=>t.DeletedAtUtc,DateTime.UtcNow));
        await Problem(await recipient.GetAsync(path),HttpStatusCode.NotFound);await Problem(await owner.GetAsync(path+"/mention-candidates?search=Test"),HttpStatusCode.NotFound);
    }
    [MySqlFact]
    public async Task CommentDatabaseScopeAndAppendOnlyHistoryCannotBeRetargeted()
    {
        await using var host=new ProjectHost(fixture,true,true,true,false,true,true);await host.InitializeAsync();var c=await SeedCompanies(host);using var owner=await host.LoginAsync(0);var path=CommentsPath(c);
        await Body(await host.MemberWriteAsync(owner,HttpMethod.Post,path,new{body="Current only"}));
        await using(var db=fixture.CreateContext())
        {
            db.TaskComments.Add(new(){WorkspaceId=c.A.Id,ProjectId=c.B1.Id,TaskId=c.TaskB.Id,AuthorId=host.Users[0].Id,Body="Wrong company task",CreatedAtUtc=host.Clock.Now.UtcDateTime});
            await Assert.ThrowsAsync<DbUpdateException>(()=>db.SaveChangesAsync());
        }
        await using(var db=fixture.CreateContext())
        {
            var revision=await db.CommentRevisions.SingleAsync(r=>r.WorkspaceId==c.A.Id);revision.Action="Edited";await Assert.ThrowsAsync<InvalidOperationException>(()=>db.SaveChangesAsync());
        }
    }

}
