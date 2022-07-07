using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace CeeFind
{
	public class SimpleMatchCollection
	{
		public List<SimpleMatch> Matches
		{
			get;
			set;
		}

		public SimpleMatchCollection()
		{
			this.Matches = new List<SimpleMatch>();
		}
	}
}