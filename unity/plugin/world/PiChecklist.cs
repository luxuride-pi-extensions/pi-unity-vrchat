using System.Text;

namespace PiUnity
{
	/// One check's outcome, so "did not run" can never be mistaken for a pass.
	///
	/// Only Fail and Warn change the verdict, and Skip counts separately again:
	/// an absent package is a fact about the project rather than a defect in it,
	/// so a report that folded it into either column would overstate or hide the
	/// finding. That is the whole reason this type exists, and it is why the two
	/// report families it serves (build-checks and materials) share it instead of
	/// each having written their own.
	internal sealed class Checklist
	{
		private readonly StringBuilder body = new StringBuilder();
		private int failures;
		private int warnings;
		private int skipped;

		public int Failures
		{
			get { return failures; }
		}

		public int Warnings
		{
			get { return warnings; }
		}

		public int Skipped
		{
			get { return skipped; }
		}

		public void Pass(string name, string detail)
		{
			body.AppendLine("  PASS  " + name + ": " + detail);
		}

		public void Fail(string name, string detail, string fix)
		{
			failures++;
			body.AppendLine("  FAIL  " + name + ": " + detail);
			body.AppendLine("        fix: " + fix);
		}

		/// A problem that does not stop the build. Counted apart from Fail so a
		/// report can say "safe, with warnings" rather than collapsing the two
		/// into one verdict nobody can act on.
		public void Warn(string name, string detail, string fix)
		{
			warnings++;
			body.AppendLine("  WARN  " + name + ": " + detail);
			body.AppendLine("        fix: " + fix);
		}

		/// A check that could not run. Deliberately not a pass and not a failure:
		/// the verdict states how many checks it left unanswered.
		public void Skip(string name, string reason)
		{
			skipped++;
			body.AppendLine("  n/a   " + name + ": not available: " + reason);
		}

		public void Detail(string line)
		{
			body.AppendLine(line);
		}

		public void Note(string line)
		{
			body.AppendLine("  " + line);
		}

		public string Render()
		{
			return body.ToString();
		}

		/// The two-column verdict, for a report whose checks are the unit. The
		/// caller supplies the wording because "1 check(s) not available" and "3
		/// materials not available" are not the same sentence.
		public string Verdict(string good, string bad)
		{
			return "VERDICT: " + (failures == 0 ? good : bad) + " -- " + failures
				+ " failure(s), " + warnings + " warning(s), " + skipped + " not available.";
		}

		/// The per-item verdict, for a report that judges N things of one kind.
		/// Zero judged is NOT a clean bill: it is the absence of an answer, and
		/// saying otherwise would be the exact confusion this type exists to
		/// prevent, so it takes its own sentence rather than reusing `good`.
		public string Verdict(int judged, string noneJudged, string good, string bad)
		{
			string result;
			if (failures > 0) result = bad;
			else if (judged == 0) result = noneJudged;
			else result = good;
			return "VERDICT: " + result + " -- " + failures + " failure(s), " + skipped + " not available.";
		}
	}
}