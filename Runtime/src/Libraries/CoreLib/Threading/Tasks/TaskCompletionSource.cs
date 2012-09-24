﻿using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Threading.Tasks {
	[Imported(IsRealType = true)]
	[IgnoreGenericArguments]
	[ScriptNamespace("ss")]
	public class TaskCompletionSource<TResult> {
		public TaskCompletionSource() {
		}

		[IntrinsicProperty]
		public Task<TResult> Task { get { return null; } }

		public void SetCanceled() {
		}

		[NonScriptable]
		public void SetException(IEnumerable<Exception> exceptions) {
		}

		public void SetException(Exception exception) {
		}

		public void SetResult(TResult result) {
		}

		public bool TrySetCanceled() {
			return false;
		}

		[NonScriptable]
		public bool TrySetException(IEnumerable<Exception> exceptions) {
			return false;
		}

		public bool TrySetException(Exception exception) {
			return false;
		}

		public bool TrySetResult(TResult result) {
			return false;
		}
	}
}
