using NUnit.Framework;

using JiraExport;
using AutoFixture.AutoNSubstitute;
using AutoFixture;
using Migration.WIContract;
using Common.Config;
using System.Collections.Generic;
using Migration.Common;
using Migration.Common.Config;
using Newtonsoft.Json.Linq;
using NSubstitute;
using System.Diagnostics.CodeAnalysis;

namespace Migration.Jira_Export.Tests
{
    [TestFixture]
    [ExcludeFromCodeCoverage]
    public class JiraMapperTests
    {
        // use auto fixture to help mock and instantiate with dummy data with nsubsitute. 
        private Fixture _fixture;

        [SetUp]
        public void Setup()
        {
            _fixture = new Fixture();
            _fixture.Customize(new AutoNSubstituteCustomization { });
        }

        [Test]
        public void When_calling_map_Then_the_expected_result_is_returned()
        {
            var jiraItem = createJiraItem();

            var expectedWiItem = new WiItem();
            expectedWiItem.Type = "User Story";
            expectedWiItem.OriginId = "issue_key";

            var sut = createJiraMapper();

            var expected = expectedWiItem;
            var actual = sut.Map(jiraItem);

            Assert.Multiple(() =>
            {
                Assert.AreEqual(expected.OriginId, actual.OriginId);
                Assert.AreEqual(expected.Type, actual.Type);
            });
        }

        [Test]
        public void When_calling_map_with_null_arguments_Then_and_exception_is_thrown()
        {
            var jiraItem = createJiraItem();
            var sut = createJiraMapper();

            Assert.Throws<System.ArgumentNullException>(() => { sut.Map((JiraItem)null); });
        }

        [Test]
        public void When_calling_maplinks_Then_the_expected_result_is_returned()
        {
            var jiraItem = _fixture.Create<JiraItem>();
            var jiraRevision = new JiraRevision(jiraItem);

            var sut = _fixture.Create<JiraMapper>();

            var expected = new List<WiLink>();
            var actual = sut.MapLinks(jiraRevision);

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void When_calling_maplinks_with_null_arguments_Then_and_exception_is_thrown()
        {
            var jiraItem = createJiraItem();
            var sut = createJiraMapper();

            Assert.Throws<System.ArgumentNullException>(() => { sut.MapLinks(null); });
        }

        [Test]
        public void When_calling_mapattachments_Then_the_expected_result_is_returned()
        {
            var jiraItem = _fixture.Create<JiraItem>();
            var jiraRevision = new JiraRevision(jiraItem);

            var sut = _fixture.Create<JiraMapper>();

            var expected = new List<WiAttachment>();
            var actual = sut.MapAttachments(jiraRevision);

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void When_calling_mapattachments_with_null_arguments_Then_and_exception_is_thrown()
        {
            var jiraItem = createJiraItem();
            var sut = createJiraMapper();

            Assert.Throws<System.ArgumentNullException>(() => { sut.MapAttachments (null); });
        }

        [Test]
        public void When_calling_mapfields_Then_the_expected_result_is_returned()
        {
            var jiraItem = createJiraItem();
            var jiraRevision = new JiraRevision(jiraItem);
            var expectedWiFieldList = new List<WiField>();

            var sut = createJiraMapper();

            var expected = expectedWiFieldList;
            var actual = sut.MapFields(jiraRevision);

            Assert.AreEqual(expected, actual);
        }

        [Test]
        public void When_calling_mapfields_with_null_arguments_Then_and_exception_is_thrown()
        {
            var jiraItem = createJiraItem();
            var sut = createJiraMapper();

            Assert.Throws<System.ArgumentNullException>(() => { sut.MapFields(null); });
        }

        [Test]
        public void When_calling_initializefieldmappings_Then_the_expected_result_is_returned()
        {
            var expectedDictionary = new Dictionary<string, FieldMapping<JiraRevision>>();
            var fieldmap = new FieldMapping<JiraRevision>();
            expectedDictionary.Add("User Story", fieldmap);

            var sut = createJiraMapper();

            var expected = expectedDictionary;
            var actual = sut.InitializeFieldMappings();

            Assert.AreEqual(expected, actual);
        }

        private JiraSettings createJiraSettings()
        {
            var settings = new JiraSettings("userID", "pass", "url", "project");
            settings.EpicLinkField = "EpicLinkField";
            settings.SprintField = "SprintField";

            return settings;
        }

        private JiraMapper createJiraMapper()
        {
            var provider = _fixture.Freeze<IJiraProvider>();
            provider.GetSettings().ReturnsForAnyArgs(createJiraSettings());

            var cjson = new ConfigJson();
            var t = new TypeMap();
            t.Types = new List<Type>();
            var f = new FieldMap();
            f.Fields = new List<Field>();
            cjson.TypeMap = t;
            var type = new Type();
            type.Source = "Story";
            type.Target = "User Story";
            t.Types.Add(type);
            cjson.FieldMap = f;

            var sut = new JiraMapper(provider, cjson);

            return sut;
        }

        private JiraItem createJiraItem()
        {
            var provider = _fixture.Freeze<IJiraProvider>();

            var issueType = JObject.Parse(@"{ 'issuetype': {'name': 'Story'}}");
            var renderedFields = JObject.Parse("{ 'custom_field_name': 'SomeValue', 'description': 'RenderedDescription' }");
            var issueKey = "issue_key";

            var remoteIssue = new JObject
            {
                { "fields", issueType },
                { "renderedFields", renderedFields },
                { "key", issueKey }
            };

            provider.DownloadIssue(default).ReturnsForAnyArgs(remoteIssue);
            provider.GetSettings().ReturnsForAnyArgs(createJiraSettings());

            var jiraItem = JiraItem.CreateFromRest(issueKey, provider);

            return jiraItem;
        }
    }
}