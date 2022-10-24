using NUnit.Framework;

using AutoFixture.AutoNSubstitute;
using AutoFixture;
using System;
using WorkItemImport;
using Migration.WIContract;
using System.Collections.Generic;
using Microsoft.TeamFoundation.WorkItemTracking.WebApi.Models;
using Microsoft.TeamFoundation.Core.WebApi;
using Microsoft.VisualStudio.Services.WebApi.Patch.Json;
using Microsoft.VisualStudio.Services.WebApi.Patch;
using System.Linq;

using Migration.Common;
using Microsoft.VisualStudio.Services.WebApi;
using System.Diagnostics.CodeAnalysis;

namespace Migration.Wi_Import.Tests
{
    [TestFixture]
    [ExcludeFromCodeCoverage]
    public class WitClientUtilsTests
    {
        private class MockedWitClientWrapper : IWitClientWrapper
        {
            private int _wiIdCounter = 1;
            public Dictionary<int, WorkItem> _wiCache = new Dictionary<int, WorkItem>();

            public MockedWitClientWrapper()
            {

            }

            public WorkItem CreateWorkItem(string wiType)
            {
                var workItem = new WorkItem();
                workItem.Id = _wiIdCounter;
                workItem.Url = $"https://example/workItems/{_wiIdCounter}";
                workItem.Fields[WiFieldReference.WorkItemType] = wiType;
                workItem.Relations = new List<WorkItemRelation>();
                _wiCache[_wiIdCounter] = (workItem);
                _wiIdCounter++;
                return workItem;
            }

            public WorkItem GetWorkItem(int wiId)
            {
                return _wiCache[wiId];
            }

            public WorkItem UpdateWorkItem(JsonPatchDocument patchDocument, int workItemId)
            {
                var wi = _wiCache[workItemId];
                foreach(var op in patchDocument)
                {
                    if(op.Operation == Operation.Add)
                    {
                        if (op.Path.StartsWith("/fields/"))
                        {
                            var field = op.Path.Replace("/fields/", "");
                            wi.Fields[field] = op.Value;
                        }
                        else if (op.Path.StartsWith("/relations/"))
                        {
                            var rel = op.Value.GetType().GetProperty("rel")?.GetValue(op.Value, null).ToString();
                            var url = op.Value.GetType().GetProperty("url")?.GetValue(op.Value, null).ToString();
                            var attributes = op.Value.GetType().GetProperty("attributes")?.GetValue(op.Value, null);
                            var comment = attributes.GetType().GetProperty("comment")?.GetValue(attributes, null).ToString();

                            var wiRelation = new WorkItemRelation();
                            wiRelation.Rel = rel;
                            wiRelation.Url = url;
                            wiRelation.Attributes = new Dictionary<string, object>{ { "comment", comment } };

                            if(wi.Relations.FirstOrDefault(r => r.Rel == wiRelation.Rel && r.Url == wiRelation.Url) == null)
                                wi.Relations.Add(wiRelation);
                        }
                    }
                    else if (op.Operation == Operation.Remove) {
                        if (op.Path.StartsWith("/fields/"))
                        {
                            var field = op.Path.Replace("/fields/", "");
                            wi.Fields[field] = "";
                        }
                        else if (op.Path.StartsWith("/relations/"))
                        {
                            var removeAtIndex = int.Parse(op.Path.Split("/").Last());
                            wi.Relations.RemoveAt(removeAtIndex);
                        }
                    }
                }
                return wi;
            }

            public TeamProject GetProject(string projectId)
            {
                var tp = new TeamProject();
                Guid projGuid;
                if(Guid.TryParse(projectId, out projGuid))
                {
                    tp.Id = projGuid;
                }
                else
                {
                    tp.Id = Guid.NewGuid();
                    tp.Name = projectId;
                }
                return tp;
            }

            public List<WorkItemRelationType> GetRelationTypes()
            {
                var hierarchyForward = new WorkItemRelationType
                {
                    ReferenceName = "System.LinkTypes.Hierarchy-Forward"
                };
                var hierarchyReverse = new WorkItemRelationType
                {
                    ReferenceName = "System.LinkTypes.Hierarchy-Reverse"
                };

                var outList = new List<WorkItemRelationType>();
                outList.Add(hierarchyForward);
                outList.Add(hierarchyReverse);
                return outList;
            }

            public AttachmentReference CreateAttachment(WiAttachment wiAttachment)
            {
                var att = new AttachmentReference();
                att.Id = Guid.NewGuid();
                att.Url = "https://example.com";
                return att;
            }
        }
        private bool MockedIsAttachmentMigratedDelegateTrue(string _attOriginId, out string attWiId)
        {
            attWiId = "1";
            return true;
        }

        private bool MockedIsAttachmentMigratedDelegateFalse(string _attOriginId, out string attWiId)
        {
            attWiId = "1";
            return false;
        }

        // use auto fixture to help mock and instantiate with dummy data with nsubsitute. 
        private Fixture _fixture;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
            _fixture.Customize(new AutoNSubstituteCustomization { });
        }

        [Test]
        public void When_calling_ensure_author_fields_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureAuthorFields(null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_author_fields_with_first_revision_Then_author_is_added_to_fields()
        {
            var rev = new WiRevision();
            rev.Fields = new List<WiField>();
            rev.Index = 0;
            rev.Author = "Firstname Lastname";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            wiUtils.EnsureAuthorFields(rev);

            Assert.That(rev.Fields[0].ReferenceName, Is.EqualTo(WiFieldReference.CreatedBy));
            Assert.That(rev.Fields[0].Value, Is.EqualTo(rev.Author));
        }

        [Test]
        public void When_calling_ensure_author_fields_with_subsequent_revision_Then_author_is_added_to_fields()
        {
            var rev = new WiRevision();
            rev.Fields = new List<WiField>();
            rev.Index = 1;
            rev.Author = "Firstname Lastname";
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            wiUtils.EnsureAuthorFields(rev);

            Assert.That(rev.Fields[0].ReferenceName, Is.EqualTo(WiFieldReference.ChangedBy));
            Assert.That(rev.Fields[0].Value, Is.EqualTo(rev.Author));
        }

        [Test]
        public void When_calling_ensure_assignee_field_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureAssigneeField(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_assignee_field_with_first_revision_Then_assignee_is_added_to_fields()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var sut = new WitClientUtils(witClientWrapper);

            var rev = new WiRevision();
            rev.Fields = new List<WiField>();
            rev.Index = 0;

            var createdWI = sut.CreateWorkItem("User Story");

            var assignedTo = new IdentityRef();
            assignedTo.UniqueName = "Mr. Test";

            createdWI.Fields[WiFieldReference.AssignedTo] = assignedTo;

            sut.EnsureAssigneeField(rev, createdWI);

            Assert.That(rev.Fields[0].ReferenceName, Is.EqualTo(WiFieldReference.AssignedTo));
            Assert.That(rev.Fields[0].Value, Is.EqualTo((createdWI.Fields[WiFieldReference.AssignedTo] as IdentityRef).UniqueName));
        }

        [Test]
        public void When_calling_ensure_date_fields_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureDateFields(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_date_fields_with_first_revision_Then_dates_are_added_to_fields()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var rev = new WiRevision();
            rev.Fields = new List<WiField>();
            rev.Index = 0;

            var createdWI = wiUtils.CreateWorkItem("User Story");
            createdWI.Fields[WiFieldReference.ChangedDate] = DateTime.Now;

            wiUtils.EnsureDateFields(rev, createdWI);

            Assert.That(rev.Fields[0].ReferenceName, Is.EqualTo(WiFieldReference.CreatedDate));
            Assert.That(
                DateTime.Parse(rev.Fields[0].Value.ToString()),
                Is.LessThan(createdWI.Fields[WiFieldReference.ChangedDate]));

            Assert.That(rev.Fields[1].ReferenceName, Is.EqualTo(WiFieldReference.ChangedDate));
            Assert.That(
                DateTime.Parse(rev.Fields[1].Value.ToString()),
                Is.EqualTo(DateTime.Parse(rev.Fields[0].Value.ToString())));
        }

        [Test]
        public void When_calling_ensure_fields_on_state_change_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureFieldsOnStateChange(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_fields_on_state_change_with_subsequent_revision_Then_dates_are_added_to_fields()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var rev = new WiRevision();
            rev.Fields = new List<WiField>();
            rev.Index = 1;

            var revState = new WiField();
            revState.ReferenceName = WiFieldReference.State;
            revState.Value = "New";
            rev.Fields.Add(revState);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            createdWI.Fields[WiFieldReference.State] = "Done";
            createdWI.Fields[WiFieldReference.ChangedDate] = DateTime.Now;

            wiUtils.EnsureFieldsOnStateChange(rev, createdWI);

            Assert.That(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.State), Is.EqualTo("New"));
            Assert.That(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.ClosedDate), Is.EqualTo(null));
            Assert.That(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.ClosedBy), Is.EqualTo(null));
            Assert.That(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.ActivatedDate), Is.EqualTo(null));
            Assert.That(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.ActivatedBy), Is.EqualTo(null));
        }

        [Test]
        public void When_calling_ensure_classification_fields_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureClassificationFields(null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_classification_fields_Then_areapath_and_iterationpath_are_added_to_fields()
        {
            var rev = new WiRevision();
            rev.Fields = new List<WiField>();

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            wiUtils.EnsureClassificationFields(rev);

            var filteredForAreaPath = rev.Fields.FindAll(f => f.ReferenceName == WiFieldReference.AreaPath && f.Value.ToString() == "");
            var filteredForIterationPath = rev.Fields.FindAll(f => f.ReferenceName == WiFieldReference.IterationPath && f.Value.ToString() == "");

            Assert.That(filteredForAreaPath.Count, Is.EqualTo(1));
            Assert.That(filteredForIterationPath.Count, Is.EqualTo(1));
        }

        [Test]
        public void When_calling_ensure_workitem_fields_initialized_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            Assert.That(
                () => wiUtils.EnsureWorkItemFieldsInitialized(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_ensure_workitem_fields_initialized_for_user_story_Then_title_and_description_are_added_to_fields()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");

            var rev = new WiRevision();
            rev.Fields = new List<WiField>();

            var revTitleField = new WiField();
            revTitleField.ReferenceName = WiFieldReference.Title;
            revTitleField.Value = "My title";

            rev.Fields.Add(revTitleField);

            wiUtils.EnsureWorkItemFieldsInitialized(rev, createdWI);

            Assert.That(createdWI.Fields[WiFieldReference.Title],
                Is.EqualTo(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.Title)));
            Assert.That(createdWI.Fields[WiFieldReference.Description],
                Is.EqualTo(""));
        }

        [Test]
        public void When_calling_ensure_workitem_fields_initialized_for_bug_Then_title_and_description_are_added_to_fields()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("Bug");

            var rev = new WiRevision();
            rev.Fields = new List<WiField>();

            var revTitleField = new WiField();
            revTitleField.ReferenceName = WiFieldReference.Title;
            revTitleField.Value = "My title";

            rev.Fields.Add(revTitleField);

            wiUtils.EnsureWorkItemFieldsInitialized(rev, createdWI);

            Assert.That(createdWI.Fields[WiFieldReference.Title],
                Is.EqualTo(rev.Fields.GetFieldValueOrDefault<string>(WiFieldReference.Title)));
            Assert.That(createdWI.Fields[WiFieldReference.ReproSteps],
                Is.EqualTo(""));
        }

        [Test]
        public void When_calling_is_duplicate_work_item_link_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var sut = new WitClientUtils(witClientWrapper);

            var result = sut.IsDuplicateWorkItemLink(null, null);
            Assert.That(result, Is.EqualTo(false));
        }

        [Test]
        public void When_calling_is_duplicate_work_item_link_with_no_containing_links_Then_false_is_returned()
        {
            var links = new WorkItemRelation[0];
            var relatedLink = new WorkItemRelation();

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);
            var isDuplicate = wiUtils.IsDuplicateWorkItemLink(links, relatedLink);

            Assert.That(isDuplicate, Is.EqualTo(false));
        }

        [Test]
        public void When_calling_create_work_item_Then_work_item_is_created_and_added_to_cache()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("Task");
            WorkItem retrievedWI = null;
            if (createdWI.Id.HasValue)
            {
                retrievedWI = wiUtils.GetWorkItem(createdWI.Id.Value);
            }

            Assert.That(createdWI.Id, Is.EqualTo(1));
            Assert.That(retrievedWI.Id, Is.EqualTo(1));

            Assert.That(createdWI.Fields[WiFieldReference.WorkItemType], Is.EqualTo("Task"));
            Assert.That(retrievedWI.Fields[WiFieldReference.WorkItemType], Is.EqualTo("Task"));
        }

        [Test]
        public void When_calling_add_link_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.AddAndSaveLink(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_add_link_with_valid_args_Then_a_link_is_added()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            var linkedWI = wiUtils.CreateWorkItem("Task");

            var link = new WiLink();
            link.WiType = "System.LinkTypes.Hierarchy-Forward";
            link.SourceOriginId = "100";
            link.SourceWiId = 1;
            link.TargetOriginId = "101";
            link.TargetWiId = 2;
            link.Change = ReferenceChangeType.Added;

            wiUtils.AddAndSaveLink(link, createdWI);

            var rel = createdWI.Relations[0];

            Assert.That(rel.Rel, Is.EqualTo(link.WiType));
            Assert.That(rel.Url, Is.EqualTo(linkedWI.Url));
        }

        [Test]
        public void When_calling_add_link_with_valid_args_and_an_attachment_is_present_on_the_created_wi_Then_a_link_is_added()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            var attachment = new WorkItemRelation();
            attachment.Title = "LinkTitle";
            attachment.Rel = "AttachedFile";
            attachment.Url = $"https://dev.azure.com/someorg/someattachment/{System.Guid.NewGuid()}";
            attachment.Attributes = new Dictionary<string, object>
            {
                    { "id", _fixture.Create<string>() },
                    { "name", "filename.png" }
            };
            createdWI.Relations.Add(attachment);
            var linkedWI = wiUtils.CreateWorkItem("Task");

            var link = new WiLink();
            link.WiType = "System.LinkTypes.Hierarchy-Forward";
            link.SourceOriginId = "100";
            link.SourceWiId = 1;
            link.TargetOriginId = "101";
            link.TargetWiId = 2;
            link.Change = ReferenceChangeType.Added;

            wiUtils.AddAndSaveLink(link, createdWI);

            var rel = createdWI.Relations.Where(rl => rl.Rel != "AttachedFile").Single();

            Assert.That(rel.Rel, Is.EqualTo(link.WiType));
            Assert.That(rel.Url, Is.EqualTo(linkedWI.Url));
        }

        [Test]
        public void When_calling_add_link_with_valid_args_and_an_attachment_is_present_on_the_linked_wi_Then_a_link_is_added()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            var linkedWI = wiUtils.CreateWorkItem("Task");
            var attachment = new WorkItemRelation();
            attachment.Title = "LinkTitle";
            attachment.Rel = "AttachedFile";
            attachment.Url = $"https://dev.azure.com/someorg/someattachment/{System.Guid.NewGuid()}";
            attachment.Attributes = new Dictionary<string, object>
            {
                    { "id", _fixture.Create<string>() },
                    { "name", "filename.png" }
            };
            linkedWI.Relations.Add(attachment);

            var link = new WiLink();
            link.WiType = "System.LinkTypes.Hierarchy-Forward";
            link.SourceOriginId = "100";
            link.SourceWiId = 1;
            link.TargetOriginId = "101";
            link.TargetWiId = 2;
            link.Change = ReferenceChangeType.Added;

            wiUtils.AddAndSaveLink(link, createdWI);

            var rel = createdWI.Relations[0];

            Assert.That(rel.Rel, Is.EqualTo(link.WiType));
            Assert.That(rel.Url, Is.EqualTo(linkedWI.Url));
        }

        [Test]
        public void When_calling_remove_link_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.RemoveAndSaveLink(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_remove_link_with_no_link_added_Then_false_is_returned()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");

            var link = new WiLink();
            link.WiType = "System.LinkTypes.Hierarchy-Forward";

            var result = wiUtils.RemoveAndSaveLink(link, createdWI);

            Assert.That(result, Is.EqualTo(false));
        }

        [Test]
        public void When_calling_remove_link_with_link_added_Then_link_is_removed()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            var linkedWI = wiUtils.CreateWorkItem("Task");

            var link = new WiLink();
            link.WiType = "System.LinkTypes.Hierarchy-Forward";
            link.SourceOriginId = "100";
            link.SourceWiId = 1;
            link.TargetOriginId = "101";
            link.TargetWiId = 2;
            link.Change = ReferenceChangeType.Added;

            wiUtils.AddAndSaveLink(link, createdWI);

            var result = wiUtils.RemoveAndSaveLink(link, createdWI);

            Assert.That(result, Is.EqualTo(true));
            Assert.That(createdWI.Relations, Is.Empty);
        }

        [Test]
        public void When_calling_correct_comment_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.CorrectComment(null, null, null, MockedIsAttachmentMigratedDelegateTrue),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_correct_comment_with_valid_args_Then_history_is_updated_with_correct_image_urls()
        {
            var commentBeforeTransformation = "My comment, including file: <img src=\"my_image.png\">";
            var commentAfterTransformation = "My comment, including file: <img src=\"https://example.com/my_image.png\">";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("Task");
            createdWI.Fields[WiFieldReference.History] = commentBeforeTransformation;
            createdWI.Relations.Add(new WorkItemRelation
            {
                Rel= "AttachedFile",
                Url= "https://example.com/my_image.png",
                Attributes = new Dictionary<string, object> { { "filePath", "C:\\Temp\\MyFiles\\my_image.png" } }
            });

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = "C:\\Temp\\MyFiles\\my_image.png";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var wiItem = new WiItem();
            wiItem.Revisions = new List<WiRevision>();
            wiItem.Revisions.Add(revision);

            wiUtils.CorrectComment(createdWI, wiItem, revision, MockedIsAttachmentMigratedDelegateTrue);

            Assert.That(createdWI.Fields[WiFieldReference.History], Is.EqualTo(commentAfterTransformation));
        }

        [Test]
        public void When_calling_correct_description_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.CorrectDescription(null, null, null, MockedIsAttachmentMigratedDelegateTrue),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_correct_description_for_user_story_Then_description_is_updated_with_correct_image_urls()
        {
            var descriptionBeforeTransformation = "My description, including file: <img src=\"my_image.png\">";
            var descriptionAfterTransformation = "My description, including file: <img src=\"https://example.com/my_image.png\">";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            createdWI.Fields[WiFieldReference.Description] = descriptionBeforeTransformation;
            createdWI.Relations.Add(new WorkItemRelation
            {
                Rel = "AttachedFile",
                Url = "https://example.com/my_image.png",
                Attributes = new Dictionary<string, object> { { "filePath", "C:\\Temp\\MyFiles\\my_image.png" } }
            });

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = "C:\\Temp\\MyFiles\\my_image.png";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var wiItem = new WiItem();
            wiItem.Revisions = new List<WiRevision>();
            wiItem.Revisions.Add(revision);

            wiUtils.CorrectDescription(createdWI, wiItem, revision, MockedIsAttachmentMigratedDelegateTrue);

            Assert.That(createdWI.Fields[WiFieldReference.Description], Is.EqualTo(descriptionAfterTransformation));
        }

        [Test]
        public void When_calling_correct_description_for_bug_Then_repro_steps_is_updated_with_correct_image_urls()
        {
            var reproStepsBeforeTransformation = "My description, including file: <img src=\"my_image.png\">";
            var reproStepsAfterTransformation = "My description, including file: <img src=\"https://example.com/my_image.png\">";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("Bug");
            createdWI.Fields[WiFieldReference.ReproSteps] = reproStepsBeforeTransformation;
            createdWI.Relations.Add(new WorkItemRelation
            {
                Rel = "AttachedFile",
                Url = "https://example.com/my_image.png",
                Attributes = new Dictionary<string, object> { { "filePath", "C:\\Temp\\MyFiles\\my_image.png" } }
            });

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = "C:\\Temp\\MyFiles\\my_image.png";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var wiItem = new WiItem();
            wiItem.Revisions = new List<WiRevision>();
            wiItem.Revisions.Add(revision);

            wiUtils.CorrectDescription(createdWI, wiItem, revision, MockedIsAttachmentMigratedDelegateTrue);

            Assert.That(createdWI.Fields[WiFieldReference.ReproSteps], Is.EqualTo(reproStepsAfterTransformation));
        }

        [Test]
        public void When_calling_apply_attachments_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.ApplyAttachments(null, null, null, MockedIsAttachmentMigratedDelegateTrue),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_apply_attachments_with_change_equal_to_added_Then_workitem_is_updated_with_correct_attachment()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = "C:\\Temp\\MyFiles\\my_image.png";
            att.AttOriginId = "100";
            att.Comment = "My comment";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var attachmentMap = new Dictionary<string, WiAttachment>();

            wiUtils.ApplyAttachments(revision, createdWI, attachmentMap, MockedIsAttachmentMigratedDelegateTrue);

            Assert.That(createdWI.Relations[0].Rel, Is.EqualTo("AttachedFile"));
            Assert.That(createdWI.Relations[0].Attributes["filePath"], Is.EqualTo(att.FilePath));
            Assert.That(createdWI.Relations[0].Attributes["comment"], Is.EqualTo(att.Comment));
        }

        [Test]
        public void When_calling_apply_attachments_with_change_equal_to_removed_Then_workitem_is_updated_with_removed_attachment()
        {
            var attachmentFilePath = "C:\\Temp\\MyFiles\\my_image.png";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            createdWI.Relations.Add(new WorkItemRelation
            {
                Rel = "AttachedFile",
                Url = "https://example.com/my_image.png",
                Attributes = new Dictionary<string, object> { { "filePath", attachmentFilePath } }
            });

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Removed;
            att.FilePath = attachmentFilePath;
            att.AttOriginId = "100";
            att.Comment = "My comment";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var attachmentMap = new Dictionary<string, WiAttachment>();

            wiUtils.ApplyAttachments(revision, createdWI, attachmentMap, MockedIsAttachmentMigratedDelegateTrue);

            Assert.That(createdWI.Relations, Is.Empty);
        }

        [Test]
        public void When_calling_apply_attachments_with_already_existing_attachment_Then_workitem_is_updated_with_another_attachment()
        {
            var attachmentFilePath = "C:\\Temp\\MyFiles\\my_image.png";

            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            createdWI.Relations.Add(new WorkItemRelation
            {
                Rel = "AttachedFile",
                Url = "https://example.com/my_image.png",
                Attributes = new Dictionary<string, object> { { "filePath", attachmentFilePath } }
            });

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = attachmentFilePath;
            att.AttOriginId = "100";
            att.Comment = "My comment";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            var attachmentMap = new Dictionary<string, WiAttachment>();

            wiUtils.ApplyAttachments(revision, createdWI, attachmentMap, MockedIsAttachmentMigratedDelegateFalse);

            Assert.That(createdWI.Relations.Count, Is.EqualTo(2));
        }
        //TODO: test SaveWorkItem

        [Test]
        public void When_calling_save_workitem_with_empty_args_Then_an_exception_is_thrown()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            Assert.That(
                () => wiUtils.SaveWorkItem(null, null),
                Throws.InstanceOf<ArgumentException>());
        }

        [Test]
        public void When_calling_save_workitem_with_populated_workitem_Then_workitem_is_updated_in_store()
        {
            var witClientWrapper = new MockedWitClientWrapper();
            var wiUtils = new WitClientUtils(witClientWrapper);

            var createdWI = wiUtils.CreateWorkItem("User Story");
            var linkedWI = wiUtils.CreateWorkItem("Task");

            // Add fields
            createdWI.Fields[WiFieldReference.Title] = "My work item";
            createdWI.Fields[WiFieldReference.Description] = "My description";
            createdWI.Fields[WiFieldReference.Priority] = "1";

            // Add attachment

            var att = new WiAttachment();
            att.Change = ReferenceChangeType.Added;
            att.FilePath = "C:\\Temp\\MyFiles\\my_image.png";
            att.AttOriginId = "100";
            att.Comment = "My comment";

            var revision = new WiRevision();
            revision.Attachments.Add(att);

            // Perform save

            wiUtils.SaveWorkItem(revision, createdWI);

            WorkItem updatedWI = null;

            if (createdWI.Id.HasValue)
            {
                updatedWI = wiUtils.GetWorkItem(createdWI.Id.Value);
            }

            // Assertions

            Assert.That(updatedWI.Fields[WiFieldReference.Title], Is.EqualTo(createdWI.Fields[WiFieldReference.Title]));
            Assert.That(updatedWI.Fields[WiFieldReference.Description], Is.EqualTo(createdWI.Fields[WiFieldReference.Description]));
            Assert.That(updatedWI.Fields[WiFieldReference.Priority], Is.EqualTo(createdWI.Fields[WiFieldReference.Priority]));

            Assert.That(createdWI.Relations[0].Rel, Is.EqualTo("AttachedFile"));
            Assert.That(createdWI.Relations[0].Url, Is.EqualTo("https://example.com"));
            Assert.That(createdWI.Relations[0].Attributes["comment"].ToString().Split('|')[0], Is.EqualTo(att.Comment));
            Assert.That(createdWI.Relations[0].Attributes["comment"].ToString().Split('|')[1], Is.EqualTo(att.FilePath));

        }
    }
}