using System;
using System.IO;
using System.Runtime.CompilerServices;

namespace CeeFind
{
	public class SearchResult
	{
		internal SimpleMatchCollection Collection
		{
			get;
			set;
		}

		internal FileInfo File
		{
			get;
			set;
		}

		internal string Line
		{
			get;
			set;
		}

		public SearchResult(SimpleMatchCollection collection, string line, FileInfo file)
		{
			this.Collection = collection;
			this.Line = line;
			this.File = file;
		}
	}
}