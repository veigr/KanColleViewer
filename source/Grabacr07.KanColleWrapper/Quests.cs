using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading.Tasks;
using Codeplex.Data;
using Nekoxy;
using Grabacr07.KanColleWrapper.Internal;
using Grabacr07.KanColleWrapper.Models;
using Grabacr07.KanColleWrapper.Models.Raw;
using System.ComponentModel;
using System.Web;

namespace Grabacr07.KanColleWrapper
{
	public class Quests : Notifier
	{
		private readonly List<Quest> allQuests;

		#region All 変更通知プロパティ

		private IReadOnlyCollection<Quest> _All;

		public IReadOnlyCollection<Quest> All
		{
			get { return this._All; }
			set
			{
				if (!Equals(this._All, value))
				{
					this._All = value;
					this.RaisePropertyChanged();
				}
			}
		}

		#endregion

		#region Current 変更通知プロパティ

		private IReadOnlyCollection<Quest> _Current;

		/// <summary>
		/// 現在遂行中の任務のリストを取得します。未取得の任務がある場合、リスト内に null が含まれることに注意してください。
		/// </summary>
		public IReadOnlyCollection<Quest> Current
		{
			get { return this._Current; }
			set
			{
				if (!Equals(this._Current, value))
				{
					this._Current = value;
					this.RaisePropertyChanged();
				}
			}
		}

		#endregion

		#region IsUntaken 変更通知プロパティ

		private bool _IsUntaken;

		public bool IsUntaken
		{
			get { return this._IsUntaken; }
			set
			{
				if (this._IsUntaken != value)
				{
					this._IsUntaken = value;
					this.RaisePropertyChanged();
				}
			}
		}

		#endregion

		#region IsEmpty 変更通知プロパティ

		private bool _IsEmpty;

		public bool IsEmpty
		{
			get { return this._IsEmpty; }
			set
			{
				if (this._IsEmpty != value)
				{
					this._IsEmpty = value;
					this.RaisePropertyChanged();
				}
			}
		}

		#endregion


		internal Quests(KanColleProxy proxy)
		{
			this.allQuests = new List<Quest>();
			this.IsUntaken = true;
			this.All = this.Current = new List<Quest>();

			proxy.api_get_member_questlist
				.Select(x => new { Page = Serialize(x), TabId = HttpUtility.ParseQueryString(x.Request.BodyAsString)["api_tab_id"] })
				.Where(x => x .Page!= null)
				.Subscribe(x => this.Update(x.Page, int.Parse(x.TabId)));

			proxy.api_req_quest_clearitemget
				.Select(x => HttpUtility.ParseQueryString(x.Request.BodyAsString)["api_quest_id"])
				.Subscribe(x => this.ClearQuest(int.Parse(x)));

			proxy.api_req_quest_stop
				.Select(x => HttpUtility.ParseQueryString(x.Request.BodyAsString)["api_quest_id"])
				.Subscribe(x => this.StopQuest(int.Parse(x)));
		}

		private static kcsapi_questlist Serialize(Session session)
		{
			try
			{
				var djson = DynamicJson.Parse(session.GetResponseAsJson());
				var questlist = new kcsapi_questlist
				{
					api_count = Convert.ToInt32(djson.api_data.api_count),
					api_disp_page = Convert.ToInt32(djson.api_data.api_disp_page),
					api_page_count = Convert.ToInt32(djson.api_data.api_page_count),
					api_exec_count = Convert.ToInt32(djson.api_data.api_exec_count),
				};

				if (djson.api_data.api_list != null)
				{
					var list = new List<kcsapi_quest>();
					var serializer = new DataContractJsonSerializer(typeof(kcsapi_quest));
					foreach (var x in (object[])djson.api_data.api_list)
					{
						try
						{
							list.Add(serializer.ReadObject(new MemoryStream(Encoding.UTF8.GetBytes(x.ToString()))) as kcsapi_quest);
						}
						catch (SerializationException ex)
						{
							// 最後のページで任務数が 5 に満たないとき、api_list が -1 で埋められるというクソ API のせい
							Debug.WriteLine(ex.Message);
						}
					}

					questlist.api_list = list.ToArray();
				}

				return questlist;
			}
			catch (Exception ex)
			{
				Debug.WriteLine(ex);
				return null;
			}
		}

		private void Update(kcsapi_questlist questlist, int tabId)
		{
			this.IsUntaken = false;

			if (tabId == 0 && questlist.api_list == null)
			{
				this.IsEmpty = true;
				this.All = this.Current = new List<Quest>();
			}
			else
			{
				var allPages = this.allQuests.GroupByPage().ToArray();
				IEnumerable<IGrouping<int, Quest>> pages;
				switch(tabId)
				{
					case 0:
						pages = this.allQuests.GroupByPage();
						break;
					case 9:
						pages = this.allQuests.GroupByPageInCurrent();
						break;
					default:
						pages = this.allQuests.GroupByPage((QuestType)tabId);
						break;
				}
				var currentPage = pages.SingleOrDefault(x => x.Key == questlist.api_disp_page - 1)?.ToArray() ?? new Quest[0];
				this.IsEmpty = false;

				Debug.WriteLine("//// LocalCurrentPage ////");
				currentPage.ToList().ForEach(x => Debug.WriteLine(x));

				Debug.WriteLine("//// LocalAllQuests.Before ////");
				allQuests.ToList().ForEach(x => Debug.WriteLine(x));

				var newPage = questlist.api_list?.Select(x => new Quest(x)).ToArray() ?? new Quest[0];
				foreach (var quest in currentPage)
				{
					if(tabId != 9)
						this.allQuests.RemoveAll(new Predicate<Quest>(x => x.Id == quest.Id));
				}
				foreach (var quest in newPage)
				{
					if(!this.allQuests.Any(x => x.Id == quest.Id))
						this.allQuests.Add(quest);
				}

				Debug.WriteLine("//// questlist.api_list ////");
				questlist.api_list?.Select(x => new Quest(x)).ToList().ForEach(x => Debug.WriteLine(x));

				Debug.WriteLine("//// LocalAllQuests.After ////");
				allQuests.ToList().ForEach(x => Debug.WriteLine(x));

				this.All = this.allQuests
						.OrderBy(x => x.Id)
						.ToList();

				var current = this.All.Where(x => x.State == QuestState.TakeOn || x.State == QuestState.Accomplished)
					.OrderBy(x => x.Id)
					.ToList();
				// 遂行中の任務数に満たない場合、未取得分として null で埋める
				while (current.Count < questlist.api_exec_count) current.Add(null);
				this.Current = current;
			}
		}

		private void ClearQuest(int id)
		{
			this.allQuests.RemoveAll(new Predicate<Quest>(x => x.Id == id));
		}

		private void StopQuest(int id)
		{
			var quest = this.allQuests.FirstOrDefault(x => x.Id == id);
			if (quest == null) return;

			this.allQuests.RemoveAll(new Predicate<Quest>(x => x.Id == id));
			var raw = quest.RawData;
			raw.api_state = (int)QuestState.None;
			this.allQuests.Add(new Quest(raw));
		}
	}

	[EditorBrowsable(EditorBrowsableState.Never)]
	static class QuestsExtensions
	{
		private static readonly int pageSize = 5;

		public static IEnumerable<IGrouping<int, Quest>> GroupByPage(this IEnumerable<Quest> source)
		{
			return source
				.OrderBy(x => x.Id)
				.Select((x, i) => new { Quest = x, Index = i })
				.GroupBy(x => x.Index / pageSize, x => x.Quest);
		}

		public static IEnumerable<IGrouping<int, Quest>> GroupByPage(this IEnumerable<Quest> source, QuestType type)
		{
			return source
				.Where(x => x.Type == type)
				.GroupByPage();
		}
		
		public static IEnumerable<IGrouping<int, Quest>> GroupByPageInCurrent(this IEnumerable<Quest> source)
		{
			return source
				.Where(x => x.State == QuestState.TakeOn || x.State == QuestState.Accomplished)
				.GroupByPage();
		}
	}
}
