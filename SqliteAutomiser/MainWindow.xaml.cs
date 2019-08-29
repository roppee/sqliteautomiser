using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
//using System.Windows.Data;
//using System.Windows.Documents;
//using System.Windows.Input;
//using System.Windows.Media;
//using System.Windows.Media.Imaging;
//using System.Windows.Navigation;
//using System.Windows.Shapes;
using System.Data.SQLite;
using System.IO;
using LumenWorks.Framework.IO.Csv;
using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using System.Globalization;

namespace SqliteAutomiser
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
        }

        private void disableButtons()
        {
            button.IsEnabled = false;
            button1.IsEnabled = false;
            button2.IsEnabled = false;
            button_db.IsEnabled = false;
            button_sqldir.IsEnabled = false;
            button_importdir.IsEnabled = false;
            button_exportdir.IsEnabled = false;
            comboBox.IsEnabled = false;
            comboBox_exporttype.IsEnabled = false;
        }
        private void enableButtons()
        {
            button.IsEnabled = true;
            button1.IsEnabled = true;
            button2.IsEnabled = true;
            button_db.IsEnabled = true;
            button_sqldir.IsEnabled = true;
            button_importdir.IsEnabled = true;
            button_exportdir.IsEnabled = true;
            comboBox.IsEnabled = true;
            comboBox_exporttype.IsEnabled = true;
        }

        private void Log(string logMessage, bool alsoPrint = true)
        {
            if (!logMessage.EndsWith("\n"))
                logMessage = logMessage + '\n';

            using (StreamWriter w = File.AppendText(Properties.Settings.Default.LOGFILE))
            {
                w.WriteLine("{0} {1} {2} {3}", DateTime.Now.ToShortDateString(),
                    DateTime.Now.ToLongTimeString(), ":", logMessage);
            }
            if (alsoPrint == true)
            {
                textBox.Dispatcher.BeginInvoke(new UpdateTextCallback(UpdateText), new object[] { logMessage }); //to update textBox from another thread
            }
        }

        //to update textBox from another thread
        public delegate void UpdateTextCallback(string message);

        //to update textBox from another thread
        private void UpdateText(string message)
        {
            textBox.AppendText(message);
        }

        private SQLiteConnection openDBconn()
        {
            SQLiteConnection conn = new SQLiteConnection(getConnStr());
            conn.Open();
            conn.EnableExtensions(true);
            try
            {
                conn.LoadExtension("libsqlitefunctions.so");
            }
            catch (SQLiteException ex)
            {
                Log("Error while loading " + AppDomain.CurrentDomain.BaseDirectory + "libsqlitefunctions.so");
                Log(ex.Message.ToString());
            }
            return conn;
            }

        private string getConnStr()
        {
            return Properties.Settings.Default.DEFCONNSTRING_OPT;
        }

        private string getNewDBConnStr(string filename)
        {
            return "Data Source=" + Properties.Settings.Default.DEFEXPORTDIR + @"\" + filename + ";Version=3;StepAPI =0;NoTXN=0;Timeout=100000;ShortNames=0;LongNames=0;NoCreat=0;NoWCHAR=0;SyncPragma=Off;FKSupport=0;JournalMode=Off;OEMCP=0;LoadExt=;BigInt=0;JDConv=0;";
        }

        private string getSqlFileDir()
        {
            return Properties.Settings.Default.DEFSQLFILEDIR;
        }

        private void processImportDir(SQLiteConnection conn, string importpath, bool checkFilesOnly = false)
        {
            string table = "";
            string[] dirs = Directory.GetDirectories(importpath);
            string[] files = new string[0];
            foreach (string dir in dirs)
            {
                table = dir.Replace(importpath, "");
                if (table != "SKIP")
                {
                    Log("Processing subdir: " + dir, true);
                    files = Directory.GetFiles(dir);
                    foreach (string file in files)
                    {
                        if (checkFilesOnly)
                            checkFile(conn, file, table);
                        else
                            importFileToDb(conn, file, table);
                    }
                }
            }
        }

        public char detectFileSeparator(TextReader reader, int rowCount)
        {
            IList<char> separators = new List<char>();
            foreach (string separator in Properties.Settings.Default.SEPARATOROPTIONS)
            {
                if (separator == "TAB")
                    separators.Add('\t');
                else
                    separators.Add(separator[0]);
            }

            IList<int> separatorsCount = new int[separators.Count];

            int character;

            int row = 0;

            bool quoted = false;
            bool firstChar = true;

            while (row < rowCount)
            {
                character = reader.Read();

                switch (character)
                {
                    case '"':
                        if (quoted)
                        {
                            if (reader.Peek() != '"') // Value is quoted and 
                                                      // current character is " and next character is not ".
                                quoted = false;
                            else
                                reader.Read(); // Value is quoted and current and 
                                               // next characters are "" - read (skip) peeked qoute.
                        }
                        else
                        {
                            if (firstChar)  // Set value as quoted only if this quote is the 
                                            // first char in the value.
                                quoted = true;
                        }
                        break;
                    case '\n':
                        if (!quoted)
                        {
                            ++row;
                            firstChar = true;
                            continue;
                        }
                        break;
                    case -1:
                        row = rowCount;
                        break;
                    default:
                        if (!quoted)
                        {
                            int index = separators.IndexOf((char)character);
                            if (index != -1)
                            {
                                ++separatorsCount[index];
                                firstChar = true;
                                continue;
                            }
                        }
                        break;
                }

                if (firstChar)
                    firstChar = false;
            }

            int maxCount = separatorsCount.Max();

            string outputstr;

            if (maxCount == 0)
                outputstr = "Unable to detect csv separator";
            else if (separators[separatorsCount.IndexOf(maxCount)] == '\t')
                outputstr = "Detected separator: T based on " + rowCount + " rows (counts: ";
            else
                outputstr = "Detected separator: " + separators[separatorsCount.IndexOf(maxCount)] + " based on " + rowCount + " rows(counts: ";

            foreach (char separator in separators)
            {
                int index = separators.IndexOf((char)separator);
                if (separator == '\t')
                    outputstr = outputstr + "TAB=" + separatorsCount[index];
                else
                    outputstr = outputstr + separator + "=" + separatorsCount[index];
                if (index < separators.Count - 1)
                    outputstr = outputstr + " ";
                else
                    outputstr = outputstr + ")";
            }

            Log(outputstr, true);
            return maxCount == 0 ? '\0' : separators[separatorsCount.IndexOf(maxCount)];
        }

        private void createTableFromFile(SQLiteConnection conn, string filepath, string table)
        {
            // now stupid void that creates all cols with double affinity, works for now but can be improved for more advanced col detection
            string commandText = "CREATE TABLE " + table + " (";
            //int columnCount = 0;

            //SQLiteDataReader reader = command.ExecuteReader();
            //while (reader.Read())
            //{
            //    commandText = commandText + '"' + reader[1] + '"' + ',';
            //    columnCount++;
            //}
            //reader.Close();

            //bool successfullyParsed = false;
           // Double dblPar = 0;

            var numberFormatInfo = new NumberFormatInfo { NumberDecimalSeparator = Properties.Settings.Default.IMPORTDECIMALSEPARATOR, NegativeSign = "\u2212", NumberNegativePattern = 1 };
            //int rowind = 1;

            //Debug.Print("Importing file: " + filepath + " to table: " + table + " columns expected: " + columnCount);

            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (CsvReader csv = new CsvReader(new StreamReader(filepath), true, Properties.Settings.Default.IMPORTCOLUMNSEPARATOR))
            {
                //int fieldCount = csv.FieldCount;
                string[] headers = csv.GetFieldHeaders();
                //rowind = rowind + 1;

                foreach (string header in headers)
                {
                    commandText = commandText + '"' + header + '"' + " DOUBLE, ";
                }

                commandText = commandText.Remove(commandText.Length - 2) + ");";
                Debug.Print(commandText);

                //while (csv.ReadNextRecord())
                //{
                //    int i = 0;
                //    foreach (SQLiteParameter par in parameters)
                //    {
                //        if (csv[i] != "" && csv[i] != "#DIV/0")
                //        {
                //            successfullyParsed = Double.TryParse(csv[i], NumberStyles.Number, numberFormatInfo, out dblPar);
                //            if (successfullyParsed)
                //            {
                //            Debug.Print("num: " + csv[i]);
                //            par.Value = dblPar;
                //            }
                //            else
                //            {
                //                //Debug.Print("str: " + csv[i]);
                //                par.Value = csv[i];
                //            }
                //        }
                //        else
                //            par.Value = DBNull.Value;
                //        i++;
                //    }
                //    try
                //    {
                //        Debug.Print("excuting query for row: " + rowind);
                //        command.ExecuteNonQuery();
                //    }
                //    catch (SQLiteException ex)
                //    {
                //        Debug.Print(ex.Message.ToString());
                //        i = 0;
                //        foreach (SQLiteParameter par in parameters)
                //        {
                //            Debug.Print(string.Format("{0} = {1};", headers[i], par.Value));
                //            i++;
                //        }

                //    }
                //    rowind = rowind + 1;
                //    Debug.Print("row: " + rowind);
                //}
            }
            textBox.AppendText('\n' + "Creating table based on file: " + filepath);

            SQLiteCommand command = new SQLiteCommand(commandText, conn);
            SQLiteTransaction trans = conn.BeginTransaction();
            command.Transaction = trans;
            //command.CommandText = commandText;
            command.ExecuteNonQuery();
            trans.Commit();

            sw.Stop();
            textBox.AppendText('\n' + "Done in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)");
            Debug.WriteLine(sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)");
        }

        //Function to check text file for correct amount of columns on each row, can be improved with additional checks
        private void checkFile(SQLiteConnection conn, string filepath, string table)
        {
            Properties.Settings.Default.IMPORTCOLUMNSEPARATOR = detectFileSeparator(new StreamReader(filepath), 5); //separator detection for each file

            Log("Checking file file: " + filepath);

            int rowind = 1;
            int rowsWithMissingColumns = 0;
            int notifyInterval = 100000;
            int notifyRow = notifyInterval;

            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (CsvReader csv = new CsvReader(new StreamReader(filepath), true, Properties.Settings.Default.IMPORTCOLUMNSEPARATOR))
            {
                csv.MissingFieldAction = MissingFieldAction.ReplaceByNull; // not really needed here, but works with detection
                string[] headers = csv.GetFieldHeaders();
                Log("Column count: " + csv.FieldCount + " headers: " + string.Join(",", headers));
                rowind++;

                while (csv.ReadNextRecord())
                {
                    for (int i = 0; i < csv.FieldCount; i++)
                    {
                        if (csv[i] == null)
                        {
                            rowsWithMissingColumns++;
                            break;
                        }
                        i++;
                    }
                    rowind++;
                    if (rowind == notifyRow)
                        Log("Checked " + rowind + " rows...");
                }
            }

            sw.Stop();
            Log("File: " + filepath + " with " + rowind + " rows (" + rowsWithMissingColumns + " with missing columns) checked in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)", true);
        }

        private void importFileToDb(SQLiteConnection conn, string filepath, string table )
        {
            Properties.Settings.Default.IMPORTCOLUMNSEPARATOR = detectFileSeparator(new StreamReader(filepath), 5); //separator detection for each file

            SQLiteCommand command = new SQLiteCommand("PRAGMA table_info(" + table + ");", conn);
            string insertText = "INSERT INTO " + table + " (";
            int columnCount = 0;

            SQLiteDataReader reader = command.ExecuteReader();
            while (reader.Read())
            {
                insertText = insertText + '"' + reader[1] + '"' + ',';
                columnCount++;
            }
            reader.Close();

            Log("Importing file: " + filepath + " to table: " + table + " columns expected: " + columnCount, true);

            SQLiteTransaction trans = conn.BeginTransaction();
            command.Transaction = trans;

            int rowind = 1;
            int rowsWithMissingColumns = 0;
            int notifyInterval = 100000;
            int notifyRow = notifyInterval;

            Stopwatch sw = new Stopwatch();
            sw.Start();

            using (CsvReader csv = new CsvReader(new StreamReader(filepath), true, Properties.Settings.Default.IMPORTCOLUMNSEPARATOR))
            {
                //int fieldCount = csv.FieldCount;
                csv.MissingFieldAction = MissingFieldAction.ReplaceByNull;
                string[] headers = csv.GetFieldHeaders();
                rowind++;

                if (columnCount == 0)
                {
                    textBox.AppendText('\n' + "Table not found, creating table: " + table);
                    createTableFromFile(conn, filepath, table);
                    foreach (string header in headers)
                    {
                        insertText = insertText + '"' + header + '"' + ',';
                        columnCount++;
                    }
                }

                insertText = insertText.Remove(insertText.Length - 1) + ") values (";
                for (int i = 0; i < columnCount; i++)
                {
                    insertText = insertText + "?,";
                }

                insertText = insertText.Remove(insertText.Length - 1) + ");";

                //textBox.AppendText('\n' + "Importing file: " + filepath);

                command.CommandText = insertText;

                List<SQLiteParameter> parameters = new List<SQLiteParameter>();
                for (int i = 0; i < columnCount; i++)
                    parameters.Add(new SQLiteParameter("@P" + i));
                foreach (SQLiteParameter par in parameters)
                    command.Parameters.Add(par);


                int colind;

                while (csv.ReadNextRecord())
                {
                    colind = 0;
                    foreach (SQLiteParameter par in parameters)
                    {
                        if (csv[colind] == null)
                        {
                            rowsWithMissingColumns++;
                            break;
                        }
                        else if (csv[colind] == "")
                            par.Value = DBNull.Value;
                        else
                            par.Value = csv[colind];
                        colind++;
                    }
                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (SQLiteException ex)
                    {
                        Log(ex.Message.ToString());
                        colind = 0;
                        foreach (SQLiteParameter par in parameters)
                        {
                            Log(string.Format("{0} = {1};", headers[colind], par.Value));
                            colind++;
                        }

                    }
                    rowind++;
                    if (rowind == notifyRow)
                    {
                        notifyRow = notifyRow + notifyInterval;
                        trans.Commit();
                        Log("Imported " + rowind + " rows...");
                        trans = conn.BeginTransaction();
                        command.Transaction = trans;
                    }
                }
            }

            trans.Commit();
            sw.Stop();
            Log("File: " + filepath + " with " + rowind + " rows (" + rowsWithMissingColumns + " with missing columns) imported to table: " + table + " in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)", true);
        }

        private void runSQL(SQLiteConnection conn, string sqlFileOrString)
        {
            if (sqlFileOrString.IndexOf(".sql") > -1) //check whether parameter is sql file path
            {
                Log("Running query file:" + '\n' + sqlFileOrString, true);
                runSQLfromString(conn, readSQLfromFileToStr(sqlFileOrString));
            }
            else //else parameter is runnable sql command
                runSQLfromString(conn, new string[] { sqlFileOrString });
        }

        private string[] readSQLfromFileToStr(string filename)
        {
            try
            {
                string contents = File.ReadAllText(filename);

                //remove comments
                char comment = Convert.ToChar(39); //comment char
                int firstCommentPos = contents.IndexOf(comment, 0);
                int secondCommentPos = contents.IndexOf(comment, firstCommentPos + 1);
                while (firstCommentPos > -1)
                {
                    contents = contents.Remove(firstCommentPos, secondCommentPos - firstCommentPos + 1);
                    firstCommentPos = contents.IndexOf(comment, 0);
                    secondCommentPos = contents.IndexOf(comment, firstCommentPos + 1);
                }

                string[] commands = contents.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                for (int i = 0; i < commands.Length; i++)
                {
                    commands[i] = Regex.Replace(commands[i], @"^\s+$[\r\n]*", "", RegexOptions.Multiline) + ';'; //remove empty rows
                }
                return commands;
            }
            catch(Exception ex)
            {
                Log(ex.Message.ToString());
                return null;
            }
        }

        private void runSQLfromString(SQLiteConnection conn, string[] sql)
        {
                foreach (string com in sql)
                {
                    SQLiteCommand command = new SQLiteCommand(com, conn);
                    Stopwatch sw = new Stopwatch();
                    sw.Start();
                    try
                    {
                        command.ExecuteNonQuery();
                    }
                    catch (SQLiteException ex)
                    {
                        Debug.WriteLine("Error in sql command:" + '\n' + com);
                        Debug.Print(ex.Message.ToString());
                    }
                }
        }

        //Run all SQL commands given in worklist file. If exportpath is given, results are exported.
        private async Task processWorklist(string worklistpath, string exportpath = "")
        {
            await Task.Run(() =>
            {
                SQLiteConnection conn = openDBconn();
                string[] worklist = File.ReadAllLines(worklistpath);
                foreach (string line in worklist)
                {
                    if (line == "import")
                        processImportDir(conn, Properties.Settings.Default.DEFIMPORTDIR + @"\");
                    else if (line == "check")
                        processImportDir(conn, Properties.Settings.Default.DEFIMPORTDIR + @"\", true);
                    else if (exportpath == "")
                        runSQL(conn, @Properties.Settings.Default.DEFSQLFILEDIR + "/" + line);
                    else
                        exportSQL(conn, @Properties.Settings.Default.DEFSQLFILEDIR + "/" + line, line.Replace(".sql", ""));
                }
                conn.Close();
            });
        }

        //Exports results of all given SQL commands to separate files starting with given filename
        private void exportSQL(SQLiteConnection conn, string sqlFileOrString, string filenameWithoutExt = "")
        {
            string[] sqlCommands;
            if (sqlFileOrString.IndexOf(".sql") > -1)
            {
                Log("Exporting query file:" + '\n' + sqlFileOrString, true);
                sqlCommands = readSQLfromFileToStr(sqlFileOrString);
            }
            else
                sqlCommands = new string[] { sqlFileOrString };

            for (int i = 0; i < sqlCommands.Length; i++)
            {
                SQLiteCommand command = new SQLiteCommand(sqlCommands[i], conn);
                if (Properties.Settings.Default.EXPORTTYPE == "csv")
                    writeSqlResultsToCsv(command, filenameWithoutExt + "_" + i.ToString("D3") + ".csv");
                else
                    writeSqlResultsToDb(command, filenameWithoutExt + "_" + i.ToString("D3") + ".db");
            }
        }

        private void writeSqlResultsToCsv(SQLiteCommand command, string filename)
        {
            string[] filename_and_ext = filename.Split(new[] { '.' });
            StreamWriter writer = new StreamWriter(Console.OpenStandardOutput()); //default output to console
            if (filename != "")
            {
                if (Properties.Settings.Default.DEFEXPORTDIR != null) //does not work atm, fix when time
                {
                    writer = new StreamWriter(@Properties.Settings.Default.DEFEXPORTDIR + "/" + filename);
                    Log("To file: " + @Properties.Settings.Default.DEFEXPORTDIR + "/" + filename, true);
                }
                else
                    writer = new StreamWriter(@AppDomain.CurrentDomain.BaseDirectory + "/" + filename);
            }
            writer.Write(getSqlResultsAsString(command));
            writer.Close();
        }

        private String getSqlResultsAsString(SQLiteCommand command)
        {
            StringBuilder sb = new StringBuilder();
            object val = null;
            try
            {
                SQLiteDataReader reader = command.ExecuteReader();

                if (Properties.Settings.Default.EXPORTCOLUMNNAMES)
                {
                    for (int j = 0; j < reader.FieldCount; j++)
                    {
                        sb.Append(reader.GetName(j));
                        if (j != (reader.FieldCount - 1))
                        {
                            sb.Append(Properties.Settings.Default.EXPORTSEPARATOR);
                        }
                    }
                    sb.Append('\n');
                }

                while (reader.Read())
                {
                    for (int j = 0; j < reader.FieldCount; j++)
                    {
                        if (reader.IsDBNull(j) == false)
                        {
                            try //very slow with try/catch, but only way to get strings out from non- type sqlite columns for now
                            {
                                val = reader.GetDouble(j);
                            }
                            catch
                            {
                                try
                                {
                                    //Debug.WriteLine("double cast failed on" + reader[j]);
                                    val = reader.GetString(j);
                                }
                                catch (SQLiteException ex)
                                {
                                    Log("Double and String cast failed on value: " + reader[j] + " with following exception:", true);
                                    Log(ex.Message.ToString(), true);
                                }
                            }
                        }
                        else
                            val = null;

                        sb.Append(val);

                        if (j != (reader.FieldCount - 1))
                            sb.Append(Properties.Settings.Default.EXPORTSEPARATOR);

                    }
                    sb.Append('\n');
                }
                reader.Close();
            }
            catch (SQLiteException ex)
            {
                Log("Export sql query failed with following exception:");
                Log(ex.Message.ToString());
            }
            return sb.ToString();
        }

        private void writeSqlResultsToDb(SQLiteCommand selectcommand, string filename)
        {

            Log("To file: " + @Properties.Settings.Default.DEFEXPORTDIR + "/" + filename, true);
            SQLiteConnection outputconn = createOutputDb(filename, selectcommand); // executes select command to create new db and output table
            if (outputconn == null)
                return;

            StringBuilder insertText = new StringBuilder();
            insertText.Append("INSERT INTO output (");

            SQLiteDataReader reader = selectcommand.ExecuteReader();

            for (int j = 0; j < reader.FieldCount; j++)
            {
                insertText.Append('"' + reader.GetName(j) + '"');
                if (j != (reader.FieldCount - 1))
                {
                    insertText.Append(',');
                }
            }

            insertText.Append(") values (");
            for (int i = 0; i < reader.FieldCount; i++)
            {
                insertText.Append("?");
                if (i != (reader.FieldCount - 1))
                {
                    insertText.Append(',');
                }
            }

            insertText.Append(");");

            SQLiteTransaction trans = outputconn.BeginTransaction();
            SQLiteCommand insertcommand = new SQLiteCommand(insertText.ToString(), outputconn);
            insertcommand.Transaction = trans;

            List<SQLiteParameter> parameters = new List<SQLiteParameter>();
            for (int i = 0; i < reader.FieldCount; i++)
                parameters.Add(new SQLiteParameter("@P" + i));
            foreach (SQLiteParameter par in parameters)
                insertcommand.Parameters.Add(par);

            int rowind = 1;
            int notifyInterval = 100000;
            int notifyRow = notifyInterval;

            try
            {
                while (reader.Read())
                {
                    int col = 0;
                    foreach (SQLiteParameter par in parameters)
                    {
                        par.ResetDbType();
                        if (reader.IsDBNull(col) == false)
                        {
                            try //very slow with try/catch, but only way to get strings out from non-string type sqlite columns for now...
                            {
                                par.Value = reader.GetDouble(col);
                            }
                            catch
                            {
                                try
                                {
                                    par.Value = reader.GetString(col);
                                }
                                catch(Exception ex)
                                {
                                    par.Value = reader[col];
                                    Log("Double and String cast failed on row: " + rowind + " col: " + reader.GetName(col) + " value: " + reader[col] + " with following exception:");
                                    Log(ex.Message.ToString());
                                }
                            }
                        }
                        else
                            par.Value = DBNull.Value;
                        col++;
                    }
                    try
                    {
                        insertcommand.ExecuteNonQuery();
                    }
                    catch (Exception ex)
                    {
                        Log(ex.Message.ToString(), true);
                        Log("Exception on output row " + rowind + ":", true);
                        col = 0;
                        StringBuilder colHeaders = new StringBuilder();
                        StringBuilder colValues = new StringBuilder();
                        foreach (SQLiteParameter par in parameters)
                        {
                            colHeaders.Append(reader.GetName(col) + ',');
                            colValues.Append(par.Value + ",");
                            col++;

                        }
                        Log(colHeaders.ToString(), true);
                        Log(colValues.ToString(), true);
                    }
                    rowind = rowind + 1;
                    if (rowind == notifyRow)
                    {
                        Log("Exported " + rowind + " rows...", true);
                        notifyRow = notifyRow + notifyInterval;
                        trans.Commit();
                        trans = outputconn.BeginTransaction();
                        insertcommand.Transaction = trans;
                    }
                }
            }
            catch (SQLiteException ex)
            {
                Log("Export sql query failed with following exception:", true);
                Log(ex.Message.ToString(), true);
            }
            reader.Close();
            trans.Commit();
            outputconn.Close();
        }

        //uses provided filename to create new database, containing output-table with column names in provided SQLiteDataReader
        private SQLiteConnection createOutputDb(string filename, SQLiteCommand selectcommand)
        {
            Log("Creating new database: " + Properties.Settings.Default.DEFEXPORTDIR + @"\" + filename);
            try
            {
                SQLiteConnection.CreateFile(Properties.Settings.Default.DEFEXPORTDIR + @"\" + filename);
            }
            catch(Exception ex)
            {
                Log(ex.Message.ToString());
                return null;
            }
            SQLiteConnection conn = new SQLiteConnection(getNewDBConnStr(filename));
            conn.Open();
            conn.EnableExtensions(true);
            conn.LoadExtension("libsqlitefunctions.so");

            //SQLiteCommand command = new SQLiteCommand("DROP TABLE IF EXISTS output;", conn);
            //command.ExecuteNonQuery();

            SQLiteCommand command = new SQLiteCommand(buildCreateTableString(selectcommand), conn);
            command.ExecuteNonQuery();
            return conn;
        }

        public class ColumnCounts
        {
            public int DoubleCount { get; set; }
            public int TextCount { get; set; }
        }

        //Builds create table statement based on output of given SQLiteCommand. Detects column types from column values, limited by maxRowsToRead 
        private string buildCreateTableString(SQLiteCommand selectcommand, int maxRowsToRead = 10000)
        {
            SQLiteDataReader reader = selectcommand.ExecuteReader();

            var columnTypeCounts  = new List<ColumnCounts>();
            for (int j = 0; j < reader.FieldCount; j++)
            {
                columnTypeCounts.Add(new ColumnCounts());
            }
            int row = 0;
            try
            {
                while (reader.Read() && row < maxRowsToRead)
                {
                    for (int j = 0; j < reader.FieldCount; j++)
                    {
                        if (reader.IsDBNull(j) == false)
                        {
                            try //very slow with try/catch, but only way to get strings out from non- type sqlite columns for now...
                            {
                                reader.GetDouble(j);
                                columnTypeCounts[j].DoubleCount++;
                            }
                            catch
                            {
                                try
                                {
                                    //Debug.WriteLine("double cast failed on" + reader[j]);
                                    reader.GetString(j);
                                    columnTypeCounts[j].TextCount++;
                                }
                                catch (SQLiteException ex)
                                {
                                    Log("Double and String cast failed on value: " + reader[j] + " with following exception:");
                                    Log(ex.Message.ToString());
                                }
                            }
                        }
                    }
                    row++;
                }
            }
            catch (SQLiteException ex)
            {
                Log("Export sql query failed with following exception:");
                Log(ex.Message.ToString());
            }

            StringBuilder logString = new StringBuilder();
            logString.Append("Dectected output column value counts: ");

            StringBuilder createText = new StringBuilder();
            createText.Append("CREATE TABLE output (");

            for (int j = 0; j < reader.FieldCount; j++)
            {
                logString.Append(" [");
                logString.Append(reader.GetName(j));
                logString.Append( " (DOUBLE=");
                logString.Append(columnTypeCounts[j].DoubleCount);
                logString.Append(",TEXT=");
                logString.Append(columnTypeCounts[j].TextCount);
                logString.Append(")]");

                createText.Append('"' + reader.GetName(j) + '"');

                if (columnTypeCounts[j].DoubleCount > columnTypeCounts[j].TextCount)
                    createText.Append(" DOUBLE");
                else
                    createText.Append(" TEXT");

                if (j != (reader.FieldCount - 1))
                {
                    createText.Append(',');
                }

            }
            reader.Close();
            createText.Append(");");

            //Log(logString.ToString());
            return createText.ToString();
        }

        private async void import_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            Stopwatch sw = new Stopwatch();
            sw.Start();
            await processWorklist(Properties.Settings.Default.DEFSQLFILEDIR + @"\import_worklist.txt");
            Log("Import worklist done in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)", true);
            enableButtons();
        }

        private async void calculate_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            Stopwatch sw = new Stopwatch();
            sw.Start();

            await processWorklist(Properties.Settings.Default.DEFSQLFILEDIR + @"\calculate_worklist.txt");
            Log("Calculate worklist done in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)");
            enableButtons();
        }

        private async void export_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            Stopwatch sw = new Stopwatch();
            sw.Start();

            await processWorklist(Properties.Settings.Default.DEFSQLFILEDIR + @"\export_worklist.txt", Properties.Settings.Default.DEFEXPORTDIR);
            Log("Export worklist done in: " + sw.Elapsed.Minutes + " Min(s) " + sw.Elapsed.Seconds + " Sec(s)", true);
            enableButtons();
        }

        private void button_db_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            OpenFileDialog openFileDialog = new OpenFileDialog();
            if (openFileDialog.ShowDialog() == true)
            {
                Properties.Settings.Default.DBPATH = openFileDialog.FileName;
                Properties.Settings.Default.DEFCONNSTRING = "Data Source=" + @Properties.Settings.Default.DBPATH + ";Version=3;";
                Properties.Settings.Default.DEFCONNSTRING_OPT = "Data Source=" + @Properties.Settings.Default.DBPATH + ";Version=3;StepAPI = 0;NoTXN=0;Timeout=100000;ShortNames=0;LongNames=0;NoCreat=0;NoWCHAR=0;SyncPragma=Off;FKSupport=0;JournalMode=Off;OEMCP=0;LoadExt=;BigInt=0;JDConv=0;";
                Properties.Settings.Default.Save();

                textBox.AppendText(Properties.Settings.Default.DEFCONNSTRING + '\n');
                SQLiteConnection conn = openDBconn();
                textBox.AppendText("Database connection ok" + '\n');
                conn.Close();
            }
            enableButtons();
        }
        private void button_sqldir_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            System.Windows.Forms.FolderBrowserDialog openFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            openFolderDialog.ShowNewFolderButton = true;
            System.Windows.Forms.DialogResult result = openFolderDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
                Properties.Settings.Default.DEFSQLFILEDIR = openFolderDialog.SelectedPath;

            Properties.Settings.Default.Save();
            enableButtons();
        }

        private void button_importdir_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            System.Windows.Forms.FolderBrowserDialog openFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            openFolderDialog.ShowNewFolderButton = true;
            System.Windows.Forms.DialogResult result = openFolderDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
                Properties.Settings.Default.DEFIMPORTDIR = openFolderDialog.SelectedPath;

            Properties.Settings.Default.Save();
            enableButtons();
        }
        private void button_exportdir_Click(object sender, RoutedEventArgs e)
        {
            disableButtons();
            System.Windows.Forms.FolderBrowserDialog openFolderDialog = new System.Windows.Forms.FolderBrowserDialog();
            openFolderDialog.ShowNewFolderButton = true;
            System.Windows.Forms.DialogResult result = openFolderDialog.ShowDialog();
            if (result == System.Windows.Forms.DialogResult.OK)
                Properties.Settings.Default.DEFEXPORTDIR = openFolderDialog.SelectedPath;

            Properties.Settings.Default.Save();
            enableButtons();
        }

        private void comboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Properties.Settings.Default.EXPORTSEPARATOR == "TAB")
                Properties.Settings.Default.EXPORTSEPARATOR = "\t";

            Properties.Settings.Default.Save();
        }
        private void comboBox_exporttype_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            Properties.Settings.Default.Save();
        }
    }
}
