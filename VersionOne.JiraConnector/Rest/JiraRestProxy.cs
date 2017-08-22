﻿using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Net;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;
using VersionOne.JiraConnector.Exceptions;

namespace VersionOne.JiraConnector.Rest
{
	public class JiraRestProxy : IJiraConnector
	{
	    private const int pageSize = 10;
	    private readonly RestClient client;

		private readonly string currentUser;

		public JiraRestProxy(string baseUrl)
			: this(baseUrl, string.Empty, string.Empty)
		{
		}

		public JiraRestProxy(string baseUrl, string username, string password)
		{
			client = new RestClient(baseUrl);

			if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
			{
				client.Authenticator = new HttpBasicAuthenticator(username, password);
			}

			currentUser = username;
		}

		public bool Validate()
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "user",
				RequestFormat = DataFormat.Json
			};
			request.AddQueryParameter("username", currentUser);

			var response = client.Execute(request);

			return response.StatusCode.Equals(HttpStatusCode.OK);
		}

		public void Login()
		{
			//throw new NotImplementedException();
		}

		public void Logout()
		{
			//throw new NotImplementedException();
		}

        private string GetIssuesFromFilterByPage(string issueFilterId, int pageNumber = 0)
	    {
	        var request = new RestRequest
	        {
	            Method = Method.GET,
	            Resource = "search",
	            RequestFormat = DataFormat.Json
	        };
	        request.AddQueryParameter("jql", $"filter={issueFilterId}");
	        request.AddQueryParameter("maxResults", pageSize.ToString());
	        request.AddQueryParameter("startAt", pageNumber.ToString());

	        var response = client.Execute(request);

	        if (response.StatusCode.Equals(HttpStatusCode.OK))
	        {
	            return response.Content;
	        }

	        if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
	            throw new JiraLoginException();
	        throw new JiraException(response.StatusDescription, new Exception(response.Content));
	    }

	    public Issue[] GetIssuesFromFilter(string issueFilterId)
	    {
	        var firstResponseContent = GetIssuesFromFilterByPage(issueFilterId);
	        var jiraIssues = new JiraIssues(JObject.Parse(firstResponseContent));

	        if (jiraIssues.TotalAvailable <= pageSize) return jiraIssues.Issues;

	        var timesToRepeat = CalculateAdditionalPages(jiraIssues);

	        for (var i = 1; i <= timesToRepeat; i++)
	        {
	            var pageNumber = i * pageSize;
	            var responseContent = GetIssuesFromFilterByPage(issueFilterId, pageNumber);
	            jiraIssues.AddIssues(JObject.Parse(responseContent));
            }

	        return jiraIssues.Issues;
	    }

        private static int CalculateAdditionalPages(JiraIssues issues)
		{
			var remainingItems = issues.TotalAvailable - pageSize;
			var timesToRepeat = remainingItems / pageSize;
			var remainder = remainingItems % pageSize;
			if (remainder > 0) timesToRepeat++;
			return timesToRepeat;
		}

		public Issue UpdateIssue(string issueKey, string fieldName, string fieldValue)
		{
			dynamic editMetadata = GetEditMetadata(issueKey);
			dynamic fieldMeta = editMetadata.fields[fieldName];

			if (fieldMeta == null)
				throw new JiraException("Field metadata is missing", null);

			var type = fieldMeta.schema["type"];
			if (type == null)
				throw new JiraException("Field metadata is missing a type", null);

			var request = new RestRequest
			{
				Method = Method.PUT,
				Resource = "issue/{issueIdOrKey}",
				RequestFormat = DataFormat.Json,
			};
			request.AddUrlSegment("issueIdOrKey", issueKey);

			dynamic body;
			if (type.ToString().Equals("array"))
			{
				var custom = fieldMeta.schema["custom"];

				if (custom != null && (custom.ToString().Equals("com.atlassian.jira.plugin.system.customfieldtypes:multiselect")))
				{
					dynamic operation = new ExpandoObject();
					((IDictionary<string, object>)operation).Add(fieldName, new List<dynamic>
					{
						new
						{
							set = new List<object> { new { value = fieldValue} }
						}
					});
					body = new { update = operation };
				}
				else
				{
					dynamic operation = new ExpandoObject();
					((IDictionary<string, object>)operation).Add(fieldName, new List<dynamic>
					{
						new
						{
							set = new List<string> { fieldValue }
						}
					});
					body = new { update = operation };
				}
			}
			else
			{
				dynamic field = new ExpandoObject();
				((IDictionary<string, object>)field).Add(fieldName, fieldValue);
				body = new { fields = field };
			}
			request.AddBody(body);

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.NoContent))
				return GetIssue(issueKey);
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public IList<Item> GetPriorities()
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "priority",
				RequestFormat = DataFormat.Json
			};

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
			{
				var data = JArray.Parse(response.Content);
				return data.Select(i => new Item(i["id"].Value<string>(), i["name"].Value<string>())).ToList();
			}
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public IList<Item> GetProjects()
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "project",
				RequestFormat = DataFormat.Json
			};

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
			{
				var data = JArray.Parse(response.Content);
				return data.Select(i => new Item(i["id"].Value<string>(), i["name"].Value<string>())).ToList();
			}
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public void AddComment(string issueKey, string comment)
		{
			var request = new RestRequest
			{
				Method = Method.POST,
				Resource = "issue/{issueIdOrKey}/comment",
				RequestFormat = DataFormat.Json,
			};
			request.AddUrlSegment("issueIdOrKey", issueKey);

			request.AddBody(new { body = comment });

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.Created))
				return;
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public void ProgressWorkflow(string issueKey, string action, string assignee)
		{
			var request = new RestRequest
			{
				Method = Method.POST,
				Resource = "issue/{issueIdOrKey}/transitions",
				RequestFormat = DataFormat.Json,
			};
			request.AddUrlSegment("issueIdOrKey", issueKey);

			dynamic body = new ExpandoObject();
			((IDictionary<string, object>)body).Add("transition", new { id = action });
			if (assignee != null)
				((IDictionary<string, object>)body).Add("fields", new { assignee = new { name = assignee } });
			request.AddBody(body);

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.NoContent))
				return;
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public IEnumerable<Item> GetAvailableActions(string issueId)
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "issue/{issueIdOrKey}/transitions?expand=transitions.fields",
				RequestFormat = DataFormat.Json
			};
			request.AddUrlSegment("issueIdOrKey", issueId);

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
			{
				dynamic data = JObject.Parse(response.Content);
				return ((JArray)data.transitions).Select(i => new Item(i["id"].Value<string>(), i["name"].Value<string>())).ToList();
			}
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		public IEnumerable<Item> GetCustomFields()
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "field",
				RequestFormat = DataFormat.Json
			};

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
			{
				var data = JArray.Parse(response.Content);
				return data.Where(i => i["custom"].Value<bool>()).Select(i => new Item(i["id"].Value<string>(), i["name"].Value<string>())).ToList();
			}
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		private dynamic GetEditMetadata(string issueIdOrKey)
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "issue/{issueIdOrKey}/editmeta",
				RequestFormat = DataFormat.Json
			};
			request.AddUrlSegment("issueIdOrKey", issueIdOrKey);

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
				return JObject.Parse(response.Content);
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

		private Issue GetIssue(string issueIdOrKey)
		{
			var request = new RestRequest
			{
				Method = Method.GET,
				Resource = "issue/{issueIdOrKey}",
				RequestFormat = DataFormat.Json
			};
			request.AddUrlSegment("issueIdOrKey", issueIdOrKey);

			var response = client.Execute(request);

			if (response.StatusCode.Equals(HttpStatusCode.OK))
			{
			    return new JiraIssues(JObject.Parse(response.Content)).Issues.First();
            }
			if (response.StatusCode.Equals(HttpStatusCode.Unauthorized))
				throw new JiraLoginException();
			throw new JiraException(response.StatusDescription, new Exception(response.Content));
		}

	    private class JiraIssues
	    {
	        public JiraIssues(dynamic responseContent)
	        {
	            Issues = ConvertToIssues(responseContent);
	            TotalAvailable = (int)responseContent.total;
	        }

	        public Issue[] Issues { get; private set; }
	        public int TotalAvailable { get; private set; }

	        public void AddIssues(dynamic responseContent)
	        {
	            var allIssues = new List<Issue>();
	            allIssues.AddRange(Issues);
	            allIssues.AddRange(ConvertToIssues(responseContent));
	            Issues = allIssues.ToArray();
	        }

	        private static Issue[] ConvertToIssues(dynamic responseContent)
	        {
	            return ((JArray)responseContent.issues).Select(CreateIssue).ToArray();
	        }

	        private static Issue CreateIssue(dynamic data)
	        {
	            return new Issue
	            {
	                Id = data.id,
	                Key = data.key,
	                Summary = data.fields.summary,
	                Description = data.fields.description,
	                Project = data.fields.project != null ? data.fields.project.name : string.Empty,
	                IssueType = data.fields.issuetype != null ? data.fields.issuetype.name : string.Empty,
	                Assignee = data.fields.assignee != null ? data.fields.assignee.name : string.Empty,
	                Priority = data.fields.priority != null ? data.fields.priority.name : string.Empty
	            };
	        }
	    }
    }
}
