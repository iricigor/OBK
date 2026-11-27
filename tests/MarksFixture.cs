using OBK.Core;

namespace OBK.Verification;

public static class MarksFixture
{
    public static void Prepare(LocalStore store, IEnumerable<Login> accounts)
    {
        foreach (var owner in accounts)
        {
            var day = new DateTimeOffset(SchoolYear.Current().StartYear, 11, 27, 14, 30, 0, TimeSpan.FromHours(1));
            Mark Assessment(string subject, string id, string text, int days, int? weight) => new()
            {
                SubjectId = subject, Id = id, MarkText = text, Weight = weight,
                MarkDate = day.AddDays(-days).ToString("O"), EditDate = day.AddDays(-days).ToString("O"),
                Caption = id == "old" ? "Fractions" : "Linear equations", Theme = "Algebra",
                TeacherId = "fictional-teacher", TypeNote = "Written test"
            };
            var old = Assessment("math", "old", "2", 3, 3);
            SubjectMarks Subject(string id, string name, string? average, params Mark[] marks) => new()
            {
                Subject = new() { Id = id, Name = name }, AverageText = average, Marks = marks.ToList()
            };
            var first = new MarksData { Subjects = [Subject("math", "Mathematics", "2,00", old)] };
            if (store.LoadMarks(owner.Url, owner.Username) is null)
                store.SaveMarks(owner.Url, owner.Username, new(first, null));
            var data = new MarksData
            {
                Subjects =
                [
                    Subject("math", "Mathematics", "1,44", old, Assessment("math", "new", "1", 0, 5),
                        Assessment("math", "homework", "2", 8, 1) with { Caption = "Homework: expressions", TypeNote = "Homework" }),
                    Subject("english", "English", "85%", Assessment("english", "points", "85", 1, null) with
                        { Caption = "Unit 3: listening", IsPoints = true, PointsText = "85/100", MaxPoints = 100 }),
                    Subject("science", "Science", null)
                ]
            };
            store.SaveMarks(owner.Url, owner.Username, new(data, new()
            {
                SemesterId = "1", From = day.AddMonths(-2).ToString("O"), To = day.AddMonths(2).ToString("O")
            }));
        }
    }
}
