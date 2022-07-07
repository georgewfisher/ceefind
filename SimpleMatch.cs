using System;
using System.Runtime.CompilerServices;

namespace CeeFind
{
	public class SimpleMatch
	{
		public int Index
		{
			get;
			set;
		}

		public int Length
		{
			get;
			set;
		}

		public string RegexMethod
        {
			get;
			set;
        }

		public SimpleMatch(int index, int length, string regexMethod)
        {
            this.Index = index;
            this.Length = length;
            this.RegexMethod = regexMethod;
        }
    }
}